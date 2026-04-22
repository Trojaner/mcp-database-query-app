using System.IO.Pipelines;
using McpDatabaseQueryApp.Apps;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.DependencyInjection;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Providers.Postgres;
using McpDatabaseQueryApp.Providers.SqlServer;
using McpDatabaseQueryApp.Server.Completions;
using McpDatabaseQueryApp.Server.Elicitation;
using McpDatabaseQueryApp.Server.Logging;
using McpDatabaseQueryApp.Server.Metadata;
using McpDatabaseQueryApp.Server.Prompts;
using McpDatabaseQueryApp.Server.Resources;
using McpDatabaseQueryApp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class InProcessServerHarness : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _services;
    private readonly Task _serverTask;
    private readonly CancellationTokenSource _cts = new();

    private InProcessServerHarness(
        string tempDir,
        ServiceProvider services,
        McpClient client,
        Task serverTask)
    {
        _tempDir = tempDir;
        _services = services;
        Client = client;
        _serverTask = serverTask;
    }

    public McpClient Client { get; }

    public static async Task<InProcessServerHarness> StartAsync(
        Dictionary<string, string?>? configOverrides = null,
        ClientCapabilities? clientCapabilities = null,
        McpClientHandlers? clientHandlers = null,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-int", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var baseConfig = new Dictionary<string, string?>
        {
            ["McpDatabaseQueryApp:MetadataDbPath"] = Path.Combine(tempDir, "meta.db"),
            ["McpDatabaseQueryApp:Secrets:KeyRef"] = "Literal:mcp-database-query-app-int-key-32bytes",
            ["McpDatabaseQueryApp:DefaultResultLimit"] = "100",
            ["McpDatabaseQueryApp:MaxResultLimit"] = "1000",
        };
        if (configOverrides is not null)
        {
            foreach (var kv in configOverrides)
            {
                baseConfig[kv.Key] = kv.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(baseConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddMcpDatabaseQueryAppCore(configuration);
        services.AddPostgresProvider();
        services.AddSqlServerProvider();
        services.AddSingleton<MetadataCache>();
        services.AddSingleton<IElicitationGateway, ElicitationGateway>();
        services.AddSingleton<CompletionRouter>();
        services.AddSingleton<McpLoggingBridge>();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var mcp = services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "McpDatabaseQueryApp.Test", Version = "0.0.1" };
        });

        mcp.WithTools<ConnectionTools>()
           .WithTools<PredefinedDbTools>()
           .WithTools<QueryTools>()
           .WithTools<SchemaTools>()
           .WithTools<ScriptTools>()
           .WithTools<UiTools>()
           .WithPrompts<McpDatabaseQueryAppPrompts>()
           .WithResources<McpDatabaseQueryAppResources>()
           .WithResources<AppResources>()
           .WithCompleteHandler(static (ctx, ct) => ctx.Services!.GetRequiredService<CompletionRouter>().HandleAsync(ctx, ct))
           .WithSetLoggingLevelHandler(static (ctx, ct) => ctx.Services!.GetRequiredService<McpLoggingBridge>().HandleSetLevelAsync(ctx, ct))
           .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMetadataStore>().InitializeAsync(cancellationToken);

        var server = provider.GetRequiredService<ModelContextProtocol.Server.McpServer>();
        var runTask = server.RunAsync(CancellationToken.None);

        var transport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "mdqa-test-client", Version = "0.0.1" },
                Capabilities = clientCapabilities ?? new ClientCapabilities
                {
                    Elicitation = new ElicitationCapability { Form = new() },
                },
                Handlers = clientHandlers ?? new McpClientHandlers(),
            },
            NullLoggerFactory.Instance,
            cancellationToken);

        return new InProcessServerHarness(tempDir, provider, client, runTask);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        catch
        {
            // best effort
        }

        _cts.Cancel();

        try
        {
            await _services.DisposeAsync();
        }
        catch
        {
            // best effort
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
