using System.IO.Pipes;
using System.Net.Sockets;
using Testcontainers.MsSql;
using Xunit;

namespace McpDatabaseQueryApp.Providers.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    public MsSqlContainer? Container { get; private set; }

    public string Host => Container is null ? "" : Container.Hostname;

    public int Port => Container?.GetMappedPublicPort(1433) ?? 0;

    public string Database => "master";

    public string Username => "sa";

    public string Password => "Strong!Pass123";

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
            Container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword(Password)
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
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

[CollectionDefinition("mssql")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
