using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using McpDatabaseQueryApp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Testcontainers.PostgreSql;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Guards the tool error surface: a failing <c>tools/call</c> must tell the caller
/// what actually went wrong.
/// </summary>
/// <remarks>
/// The SDK replaces the message of any non-<c>McpException</c> with the opaque
/// <c>"An error occurred invoking '&lt;tool&gt;'."</c> placeholder, so a regression
/// here is silent — the calls still fail, they just stop explaining themselves.
/// Every assertion below therefore checks for the specific cause, not merely that
/// <see cref="CallToolResult.IsError"/> was set.
/// </remarks>
public sealed class ToolErrorReportingTests
{
    internal static string ErrorText(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        // The SDK emits exactly "An error occurred invoking '<tool>'." when it
        // cannot forward a message; anything with a ": <detail>" tail is real.
        Regex.IsMatch(text, @"^An error occurred invoking '[^']*'\.$").Should().BeFalse(
            because: "the SDK's generic placeholder means the real cause never reached the client");
        return text;
    }

    private static async Task<string> CallExpectingErrorAsync(
        InProcessServerHarness harness,
        string tool,
        Dictionary<string, object?> arguments)
    {
        var result = await harness.Client.CallToolAsync(tool, arguments);
        return ErrorText(result);
    }

    [Fact]
    public async Task Unknown_connection_id_reports_what_was_not_found()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var text = await CallExpectingErrorAsync(harness, "db_ping", new() { ["connectionId"] = "no-such-conn" });

        text.Should().Contain("Not found").And.Contain("no-such-conn");
    }

    [Fact]
    public async Task Unknown_predefined_database_reports_the_name()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var text = await CallExpectingErrorAsync(harness, "db_predefined_get", new() { ["nameOrId"] = "ghost-db" });

        text.Should().Contain("Not found").And.Contain("ghost-db");
    }

    [Fact]
    public async Task Missing_required_argument_names_the_argument()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        // Binding failures happen before the tool body runs, so only the
        // server-wide call-tool filter can rescue this message.
        var text = await CallExpectingErrorAsync(harness, "db_predefined_get", new());

        text.Should().Contain("Invalid parameters").And.Contain("nameOrId");
    }

    [Fact]
    public async Task Wrong_argument_type_reports_a_parse_failure()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var text = await CallExpectingErrorAsync(harness, "db_ping", new() { ["connectionId"] = 42 });

        text.Should().Contain("Invalid parameters");
    }

    [Fact]
    public async Task Unknown_provider_is_reported_verbatim()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var text = await CallExpectingErrorAsync(harness, "db_connect", new()
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["provider"] = "Oracle",
                ["host"] = "db.example.com",
                ["database"] = "d",
                ["username"] = "u",
                ["password"] = "p",
            },
        });

        text.Should().Contain("Oracle");
    }

    [Fact]
    public async Task Incomplete_adhoc_descriptor_reports_the_missing_fields()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var text = await CallExpectingErrorAsync(harness, "db_connect", new()
        {
            ["args"] = new Dictionary<string, object?> { ["provider"] = "Postgres" },
        });

        text.Should().Contain("Invalid parameters").And.Contain("host");
    }

    [Fact]
    public async Task Refused_connection_reports_the_socket_cause()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        // Port 1 on loopback is reliably closed, so the driver fails at connect
        // time with a SocketException nested under the Npgsql exception.
        var text = await CallExpectingErrorAsync(harness, "db_connect", new()
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["provider"] = "Postgres",
                ["host"] = "127.0.0.1",
                ["port"] = 1,
                ["database"] = "nope",
                ["username"] = "u",
                ["password"] = "p",
            },
        });

        text.Should().Contain("Connection failed")
            .And.Contain("127.0.0.1:1")
            .And.Contain("ConnectionRefused");
    }

    [Fact]
    public async Task Failed_connection_does_not_leak_the_password()
    {
        await using var harness = await InProcessServerHarness.StartAsync();
        const string sentinel = "k9ZGuardedPasswordHunter2024";

        var text = await CallExpectingErrorAsync(harness, "db_connect", new()
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["provider"] = "Postgres",
                ["host"] = "127.0.0.1",
                ["port"] = 1,
                ["database"] = "nope",
                ["username"] = "u",
                ["password"] = sentinel,
            },
        });

        text.Should().NotContain(sentinel);
    }
}

/// <summary>
/// Direct tests for the sanitizing layer, covering inputs that are awkward to
/// provoke through a real driver.
/// </summary>
public sealed class ToolErrorSanitizationTests
{
    private static string Message(Exception ex) =>
        ToolErrorHandler.ToMcpException(ex, NullLogger.Instance).Message;

    [Theory]
    [InlineData("Password=hunter2")]
    [InlineData("password=hunter2;")]
    [InlineData("PWD=hunter2")]
    [InlineData("Password = \"hunter2\"")]
    [InlineData("api_key=hunter2")]
    public void Credential_keywords_are_masked(string fragment)
    {
        var message = Message(new InvalidOperationException($"connect failed using Host=db;{fragment};Database=app"));

        message.Should().NotContain("hunter2");
        message.Should().Contain("***");
    }

    [Fact]
    public void Redaction_keeps_the_diagnostic_parts_of_the_message()
    {
        // Host, port, and database are supplied by the caller and are what makes
        // a connection error actionable; only the secret is masked.
        var message = Message(new InvalidOperationException("connect failed using Host=db.internal;Port=5432;Password=hunter2;Database=app"));

        message.Should().Contain("db.internal").And.Contain("5432").And.Contain("app");
    }

    [Fact]
    public void Inner_exception_causes_are_appended()
    {
        var ex = new InvalidOperationException("Failed to connect", new TimeoutException("The operation timed out"));

        Message(ex).Should().Contain("Failed to connect").And.Contain("The operation timed out");
    }

    [Fact]
    public void Overlong_messages_are_truncated()
    {
        var message = Message(new InvalidOperationException(new string('x', 10_000)));

        message.Length.Should().BeLessThan(2_200);
        message.Should().EndWith("(truncated; see server logs)");
    }

    [Fact]
    public void An_existing_mcp_exception_is_not_rewrapped()
    {
        var original = new McpException("already curated");

        ToolErrorHandler.ToMcpException(original, NullLogger.Instance).Should().BeSameAs(original);
    }
}

/// <summary>
/// The subset of error reporting that needs a live server to produce a genuine
/// driver error (bad SQL, wrong credentials) rather than a connect-time failure.
/// </summary>
[Collection("live-postgres")]
public sealed class LiveDatabaseErrorReportingTests
{
    private readonly LivePostgresFixture _fixture;

    public LiveDatabaseErrorReportingTests(LivePostgresFixture fixture) => _fixture = fixture;

    private async Task<(InProcessServerHarness Harness, string ConnectionId)> ConnectAsync(bool readOnly = false)
    {
        var harness = await InProcessServerHarness.StartAsync();
        var result = await harness.Client.CallToolAsync("db_connect", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["provider"] = "Postgres",
                ["host"] = _fixture.Host,
                ["port"] = _fixture.Port,
                ["database"] = LivePostgresFixture.Database,
                ["username"] = LivePostgresFixture.Username,
                ["password"] = LivePostgresFixture.Password,
                ["readOnly"] = readOnly,
                // A write-enabled connection needs an explicit confirmation; the
                // harness runs with --dangerously-skip-permissions so the flag is
                // honoured instead of prompting.
                ["confirm"] = true,
            },
        });

        result.IsError.Should().NotBe(
            true,
            because: string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));

        var payload = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        var id = JsonDocument.Parse(payload).RootElement.GetProperty("connectionId").GetString();
        id.Should().NotBeNullOrEmpty();
        return (harness, id!);
    }

    [SkippableFact]
    public async Task Unknown_relation_reports_the_postgres_sqlstate_and_message()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker is not available.");
        var (harness, connectionId) = await ConnectAsync();
        await using var _ = harness;

        var result = await harness.Client.CallToolAsync("db_query", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["connectionId"] = connectionId,
                ["sql"] = "SELECT * FROM table_that_does_not_exist",
            },
        });

        var text = ToolErrorReportingTests.ErrorText(result);
        // 42P01 is undefined_table. Forwarding the SQLSTATE lets the caller
        // distinguish "typo in table name" from a transport failure.
        text.Should().Contain("42P01").And.Contain("table_that_does_not_exist");
    }

    [SkippableFact]
    public async Task Malformed_sql_reports_a_syntax_error()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker is not available.");
        var (harness, connectionId) = await ConnectAsync();
        await using var _ = harness;

        var result = await harness.Client.CallToolAsync("db_query", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["connectionId"] = connectionId,
                ["sql"] = "SELECT FROM WHERE",
            },
        });

        ToolErrorReportingTests.ErrorText(result).Should().ContainEquivalentOf("syntax error");
    }

    [SkippableFact]
    public async Task Bad_password_reports_an_authentication_failure()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker is not available.");
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("db_connect", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["provider"] = "Postgres",
                ["host"] = _fixture.Host,
                ["port"] = _fixture.Port,
                ["database"] = LivePostgresFixture.Database,
                ["username"] = LivePostgresFixture.Username,
                ["password"] = "definitely-not-the-password",
            },
        });

        var text = ToolErrorReportingTests.ErrorText(result);
        text.Should().ContainEquivalentOf("authentication");
        text.Should().NotContain("definitely-not-the-password");
    }
}

public sealed class LivePostgresFixture : IAsyncLifetime
{
    public const string Database = "mdqa_errors";
    public const string Username = "postgres";
    public const string Password = "postgres";

    private PostgreSqlContainer? _container;

    public bool DockerAvailable { get; private set; }

    public string Host => _container?.Hostname ?? string.Empty;

    public int Port => _container?.GetMappedPublicPort(5432) ?? 0;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithUsername(Username)
                .WithPassword(Password)
                .WithDatabase(Database)
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _container.StartAsync(cts.Token);
            DockerAvailable = true;
        }
        catch
        {
            DockerAvailable = false;
            if (_container is not null)
            {
                try
                {
                    await _container.DisposeAsync();
                }
                catch
                {
                    // best effort
                }

                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("live-postgres")]
public sealed class LivePostgresCollection : ICollectionFixture<LivePostgresFixture>;
