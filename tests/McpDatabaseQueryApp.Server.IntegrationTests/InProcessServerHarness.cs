using System.IO.Pipelines;
using McpDatabaseQueryApp.Apps;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.DependencyInjection;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.DependencyInjection;
using McpDatabaseQueryApp.Server.IntegrationTests.Profiles;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// In-process MCP harness used by the integration test suite. Wires a
/// production-shaped service graph onto a paired <see cref="Pipe"/> transport
/// so an <see cref="McpClient"/> can drive the server end-to-end without
/// process boundaries.
/// </summary>
/// <remarks>
/// <para>
/// The harness installs two test seams in place of their production
/// counterparts:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="TestProfileContextAccessor"/> — replaces the
///   <see cref="AsyncLocal{T}"/>-backed accessor with a volatile field so the
///   profile can be switched from the test thread and still be visible to the
///   server's already-scheduled <c>RunAsync</c> continuations.</description></item>
///   <item><description><see cref="TestProfileResolver"/> — replaces the OAuth2
///   resolver chain with a deterministic "use this profile" resolver. The HTTP
///   transport's <c>ProfileResolutionMiddleware</c> still calls into it per
///   request; the in-process stream transport doesn't run middleware, hence
///   the volatile-field accessor above.</description></item>
/// </list>
/// </remarks>
public sealed class InProcessServerHarness : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _services;
    private readonly Task _serverTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly TestProfileContextAccessor _accessor;
    private readonly TestProfileResolver _resolver;

    private InProcessServerHarness(
        string tempDir,
        ServiceProvider services,
        McpClient client,
        Task serverTask,
        TestProfileContextAccessor accessor,
        TestProfileResolver resolver)
    {
        _tempDir = tempDir;
        _services = services;
        Client = client;
        _serverTask = serverTask;
        _accessor = accessor;
        _resolver = resolver;
    }

    /// <summary>
    /// The MCP client wired to the in-process server.
    /// </summary>
    public McpClient Client { get; }

    /// <summary>
    /// The DI container backing the in-process server. Tests use this to
    /// reach into stores (e.g. <see cref="IProfileStore"/>) for setup.
    /// </summary>
    public IServiceProvider Services => _services;

    /// <summary>
    /// Switches the ambient profile that subsequent
    /// <see cref="McpClient.CallToolAsync"/> invocations execute under.
    /// Provisions the profile in the store on first use.
    /// </summary>
    public async Task UseProfileAsync(ProfileId profileId, string? subject = null, CancellationToken cancellationToken = default)
    {
        var store = _services.GetRequiredService<IProfileStore>();
        var profile = await store.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            profile = new Profile(
                profileId,
                Name: subject ?? profileId.Value,
                Subject: subject ?? profileId.Value,
                Issuer: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Status: ProfileStatus.Active,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal));
            profile = await store.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        _accessor.Set(profile);
        _resolver.Set(profile);
    }

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
            ["McpDatabaseQueryApp:DangerouslySkipPermissions"] = "true",
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
        services.AddProfiles();

        // Test seams: replace the AsyncLocal accessor with a volatile-field
        // accessor and the OAuth2 composite resolver with a deterministic
        // single-profile resolver. Both are settable per-test via UseProfile.
        var accessor = new TestProfileContextAccessor();
        services.RemoveAll<IProfileContextAccessor>();
        services.AddSingleton<IProfileContextAccessor>(accessor);

        services.AddPostgresProvider();
        services.AddSqlServerProvider();
        services.AddQueryExecutionPipeline();
        services.AddAclAuthorization(configuration);
        services.AddDataIsolation(configuration);
        services.AddHostedService<Hosting.IsolationRuleBootstrap>();
        services.AddMcpDestructiveOperationConfirmer();

        // Register the static-entry bootstrap as both the IAclStaticEntrySource
        // and a hosted service so config-driven entries flow into the evaluator.
        services.AddSingleton<McpDatabaseQueryApp.Server.Hosting.AclBootstrapHostedService>();
        services.AddSingleton<McpDatabaseQueryApp.Core.Authorization.IAclStaticEntrySource>(
            sp => sp.GetRequiredService<McpDatabaseQueryApp.Server.Hosting.AclBootstrapHostedService>());
        services.AddSingleton<MetadataCache>();
        services.AddSingleton<IElicitationGateway, ElicitationGateway>();
        services.AddSingleton<CompletionRouter>();
        services.AddSingleton<McpLoggingBridge>();
        services.AddSingleton<MutationGuard>();
        services.AddSingleton<ScriptPromptProvider>();

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
           .WithTools<NoteTools>()
           .WithPrompts<McpDatabaseQueryAppPrompts>()
           .WithResources<McpDatabaseQueryAppResources>()
           .WithResources<AppResources>()
           .WithCompleteHandler(static (ctx, ct) => ctx.Services!.GetRequiredService<CompletionRouter>().HandleAsync(ctx, ct))
           .WithSetLoggingLevelHandler(static (ctx, ct) => ctx.Services!.GetRequiredService<McpLoggingBridge>().HandleSetLevelAsync(ctx, ct))
           .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMetadataStore>().InitializeAsync(cancellationToken);
        var profileStore = provider.GetRequiredService<IProfileStore>();
        await profileStore.EnsureDefaultAsync(cancellationToken);

        // BuildServiceProvider does not run IHostedServices automatically.
        // Manually start the ones the harness depends on (ACL static-entry
        // bootstrap, isolation-rule bootstrap, etc.). The ACL bootstrap is
        // also registered as a singleton so we add it explicitly in case it
        // wasn't picked up by GetServices<IHostedService>.
        await provider.GetRequiredService<McpDatabaseQueryApp.Server.Hosting.AclBootstrapHostedService>()
            .StartAsync(cancellationToken);
        foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
        {
            await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        var defaultProfile = await profileStore.GetAsync(ProfileId.Default, cancellationToken)
            ?? throw new InvalidOperationException("Default profile missing.");
        accessor.Set(defaultProfile);
        var resolver = new TestProfileResolver(defaultProfile);

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

        return new InProcessServerHarness(tempDir, provider, client, runTask, accessor, resolver);
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
