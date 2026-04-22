using System.IO.Pipes;
using System.Net.Sockets;
using Testcontainers.PostgreSql;
using Xunit;

namespace McpDatabaseQueryApp.Providers.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer? Container { get; private set; }

    public string ConnectionString => Container?.GetConnectionString() ?? string.Empty;

    public string Host => Container is null ? "" : Container.Hostname;

    public int Port => Container?.GetMappedPublicPort(5432) ?? 0;

    public string Database => "mdqa_test";

    public string Username => "postgres";

    public string Password => "postgres";

    public bool DockerAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        if (!CanReachDockerQuickly())
        {
            DockerAvailable = false;
            return;
        }

        try
        {
            Container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithUsername(Username)
                .WithPassword(Password)
                .WithDatabase(Database)
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await Container.StartAsync(cts.Token);
            DockerAvailable = true;
        }
        catch
        {
            DockerAvailable = false;
            if (Container is not null)
            {
                try
                {
                    await Container.DisposeAsync();
                }
                catch
                {
                    // best effort
                }

                Container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }

    private static bool CanReachDockerQuickly()
    {
        try
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                using var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(300);
                return pipe.IsConnected;
            }

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connectTask = client.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"));
            return connectTask.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
