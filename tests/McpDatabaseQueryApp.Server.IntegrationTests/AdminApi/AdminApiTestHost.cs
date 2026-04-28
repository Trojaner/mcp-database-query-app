using McpDatabaseQueryApp.Core.DependencyInjection;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.AdminApi;
using McpDatabaseQueryApp.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.IntegrationTests.AdminApi;

/// <summary>
/// Minimal HTTP host that exposes only the admin REST API. Reuses the same
/// DI wiring as the production composition root but skips MCP registration:
/// the admin API is independent of the MCP transport.
/// </summary>
/// <remarks>
/// Each instance uses a unique SQLite file under <c>%TEMP%/mcp-admin-int</c>
/// so tests do not race over shared metadata. Disposal removes the temp dir.
/// </remarks>
public sealed class AdminApiTestHost : IAsyncDisposable
{
    private const string DefaultApiKey = "test-admin-key-1234567890";

    private readonly string _tempDir;
    private readonly WebApplication _app;

    private AdminApiTestHost(string tempDir, WebApplication app, HttpClient client, string apiKey)
    {
        _tempDir = tempDir;
        _app = app;
        Client = client;
        ApiKey = apiKey;
    }

    /// <summary>HTTP client preconfigured with <c>https://localhost</c> base address.</summary>
    public HttpClient Client { get; }

    /// <summary>The admin API key the host was configured with.</summary>
    public string ApiKey { get; }

    /// <summary>The DI container backing the host.</summary>
    public IServiceProvider Services => _app.Services;

    public static async Task<AdminApiTestHost> StartAsync(
        Dictionary<string, string?>? configOverrides = null,
        string? apiKey = null,
        bool requireHttps = false,
        IReadOnlyList<string>? allowedHosts = null,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp-admin-int", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var resolvedKey = apiKey ?? DefaultApiKey;

        var baseConfig = new Dictionary<string, string?>
        {
            ["McpDatabaseQueryApp:MetadataDbPath"] = Path.Combine(tempDir, "meta.db"),
            ["McpDatabaseQueryApp:Secrets:KeyRef"] = "Literal:mcp-database-query-app-int-key-32bytes",
            ["McpDatabaseQueryApp:DefaultResultLimit"] = "100",
            ["McpDatabaseQueryApp:MaxResultLimit"] = "1000",
            ["McpDatabaseQueryApp:DangerouslySkipPermissions"] = "true",
            ["McpDatabaseQueryApp:Authorization:DefaultProfilePolicy"] = "DenyAll",
            ["McpDatabaseQueryApp:AdminApi:Enabled"] = "true",
            ["McpDatabaseQueryApp:AdminApi:ApiKey"] = resolvedKey,
            ["McpDatabaseQueryApp:AdminApi:RequireHttps"] = requireHttps ? "true" : "false",
        };
        // Configuration binding for collections appends; build the allow-list
        // from a single source so an explicit override fully replaces the
        // default localhost allow-list.
        var hosts = allowedHosts ?? new[] { "localhost", "127.0.0.1" };
        for (var i = 0; i < hosts.Count; i++)
        {
            baseConfig[$"McpDatabaseQueryApp:AdminApi:AllowedHosts:{i}"] = hosts[i];
        }

        if (configOverrides is not null)
        {
            foreach (var kv in configOverrides)
            {
                baseConfig[kv.Key] = kv.Value;
            }
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AdminApiTestHost).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(baseConfig);
        builder.Logging.ClearProviders();

        var configuration = builder.Configuration;
        builder.Services.AddSingleton<IConfiguration>(configuration);
        builder.Services.AddMcpDatabaseQueryAppCore(configuration);
        builder.Services.AddAclAuthorization(configuration);
        builder.Services.AddDataIsolation(configuration);
        builder.Services.AddHostedService<IsolationRuleBootstrap>();
        builder.Services.AddSingleton<AclBootstrapHostedService>();
        builder.Services.AddSingleton<McpDatabaseQueryApp.Core.Authorization.IAclStaticEntrySource>(
            sp => sp.GetRequiredService<AclBootstrapHostedService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AclBootstrapHostedService>());
        builder.Services.AddAdminApi(configuration);

        // Replace Kestrel with TestServer.
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        await InitializeAsync(app, cancellationToken).ConfigureAwait(false);

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAdminApi();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");
        return new AdminApiTestHost(tempDir, app, client, resolvedKey);
    }

    private static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var store = app.Services.GetRequiredService<IMetadataStore>();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var profiles = app.Services.GetRequiredService<IProfileStore>();
        await profiles.EnsureDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public HttpRequestMessage NewRequest(HttpMethod method, string relativeUri)
    {
        var req = new HttpRequestMessage(method, relativeUri);
        req.Headers.Add(AdminApiKeyAuthenticationHandler.HeaderName, ApiKey);
        return req;
    }

    public async ValueTask DisposeAsync()
    {
        try { Client.Dispose(); } catch { /* best effort */ }
        try { await _app.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
        try { await _app.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
