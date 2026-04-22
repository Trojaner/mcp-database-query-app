using McpDatabaseQueryApp.Apps;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.DependencyInjection;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Providers.Postgres;
using McpDatabaseQueryApp.Providers.SqlServer;
using McpDatabaseQueryApp.Server.Completions;
using McpDatabaseQueryApp.Server.Elicitation;
using McpDatabaseQueryApp.Server.Hosting;
using McpDatabaseQueryApp.Server.Logging;
using McpDatabaseQueryApp.Server.Metadata;
using McpDatabaseQueryApp.Server.Prompts;
using McpDatabaseQueryApp.Server.Resources;
using McpDatabaseQueryApp.Server.Tools;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using McpDatabaseQueryApp.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var bootstrap = new ConfigurationManager();
bootstrap.AddJsonFile("appsettings.json", optional: true);
bootstrap.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
bootstrap.AddEnvironmentVariables();
bootstrap.AddCommandLine(args);

var appOptions = bootstrap.GetSection(McpDatabaseQueryAppOptions.SectionName).Get<McpDatabaseQueryAppOptions>() ?? new McpDatabaseQueryAppOptions();

if (args.Contains("--dangerously-skip-permissions", StringComparer.OrdinalIgnoreCase))
{
    appOptions.DangerouslySkipPermissions = true;
}

if (appOptions.Transport.Http.Enabled)
{
    await RunWebAsync(args, appOptions).ConfigureAwait(false);
}
else
{
    await RunStdioAsync(args, appOptions).ConfigureAwait(false);
}

static async Task RunStdioAsync(string[] args, McpDatabaseQueryAppOptions options)
{
    var builder = Host.CreateApplicationBuilder(args);
    ConfigureLogging(builder.Logging);
    ConfigureServices(builder.Services, builder.Configuration, includeHttp: false);

    var host = builder.Build();
    await InitializeStoreAsync(host.Services).ConfigureAwait(false);
    try
    {
        await host.RunAsync().ConfigureAwait(false);
    }
    catch (TaskCanceledException)
    {
        // stdio pipe closed; normal shutdown
    }

    _ = options; // placeholder for future option-driven branching
}

static async Task RunWebAsync(string[] args, McpDatabaseQueryAppOptions options)
{
    var builder = WebApplication.CreateSlimBuilder(args);
    ConfigureLogging(builder.Logging);
    ConfigureServices(builder.Services, builder.Configuration, includeHttp: true);
    if (!string.IsNullOrWhiteSpace(options.Transport.Http.Urls))
    {
        builder.WebHost.UseUrls(options.Transport.Http.Urls);
    }

    var app = builder.Build();
    await InitializeStoreAsync(app.Services).ConfigureAwait(false);
    app.MapMcp();
    await app.RunAsync().ConfigureAwait(false);
}

static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

static void ConfigureServices(IServiceCollection services, IConfiguration configuration, bool includeHttp)
{
    services.AddMcpDatabaseQueryAppCore(configuration);
    services.AddPostgresProvider();
    services.AddSqlServerProvider();
    services.AddSingleton<MetadataCache>();
    services.AddSingleton<IElicitationGateway, ElicitationGateway>();
    services.AddSingleton<CompletionRouter>();
    services.AddSingleton<McpLoggingBridge>();
    services.AddSingleton<ScriptPromptProvider>();
    services.AddSingleton<MutationGuard>();
    services.AddHostedService<ResultSetJanitor>();
    services.AddHostedService<ConnectionReaper>();

    // Per the MCP SDK docs, the correct way to make tool schema generation see
    // user DTOs is to pass a preconfigured JsonSerializerOptions into each
    // WithTools<T>(options) call. The SDK's own resolver goes first so protocol
    // types (including experimental properties) keep their SDK contract; our
    // source-gen context covers the McpDatabaseQueryApp DTOs; the reflection resolver is
    // kept as a trailing fallback for `object?` query-row values that can't be
    // described statically (see McpDatabaseQueryAppJsonContext).
    var toolSerializerOptions = new JsonSerializerOptions
    {
        TypeInfoResolverChain =
        {
            McpJsonUtilities.DefaultOptions.TypeInfoResolver!,
            McpDatabaseQueryAppJsonContext.Default,
            new DefaultJsonTypeInfoResolver(),
        },
    };

    var mcp = services.AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "MCP Database Query App",
            Version = "0.1.0",
        };
        options.ServerInstructions = "MCP Database Query App is a database management server for PostgreSQL and SQL Server. Use db_list_predefined to discover configured databases and db_connect to open a session.";
    });

    mcp.WithTools<ConnectionTools>(toolSerializerOptions)
       .WithTools<PredefinedDbTools>(toolSerializerOptions)
       .WithTools<QueryTools>(toolSerializerOptions)
       .WithTools<SchemaTools>(toolSerializerOptions)
       .WithTools<ScriptTools>(toolSerializerOptions)
       .WithTools<UiTools>(toolSerializerOptions)
       .WithTools<NoteTools>(toolSerializerOptions)
       .WithListPromptsHandler(async (ctx, ct) =>
       {
           var provider = ctx.Services!.GetRequiredService<ScriptPromptProvider>();
           var scripts = await provider.ListAsync(ct).ConfigureAwait(false);
           return scripts;
       })
       .WithGetPromptHandler(async (ctx, ct) =>
       {
           var provider = ctx.Services!.GetRequiredService<ScriptPromptProvider>();
           var args = ctx.Params?.Arguments?.ToDictionary(
               kvp => kvp.Key,
               kvp => kvp.Value.GetString() ?? kvp.Value.ToString(),
               StringComparer.Ordinal) as IReadOnlyDictionary<string, string>;
           return await provider.GetAsync(ctx.Params!.Name, args, ct).ConfigureAwait(false);
       })
       .WithResources<McpDatabaseQueryAppResources>()
       .WithResources<AppResources>()
       .WithCompleteHandler(static (ctx, ct) =>
           ctx.Services!.GetRequiredService<CompletionRouter>().HandleAsync(ctx, ct))
       .WithSetLoggingLevelHandler(static (ctx, ct) =>
           ctx.Services!.GetRequiredService<McpLoggingBridge>().HandleSetLevelAsync(ctx, ct));

    if (includeHttp)
    {
        mcp.WithHttpTransport();
    }
    else
    {
        mcp.WithStdioServerTransport();
    }
}

static async Task InitializeStoreAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<IMetadataStore>();
    await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
}
