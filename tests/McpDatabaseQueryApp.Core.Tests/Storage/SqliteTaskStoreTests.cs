using FluentAssertions;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Core.Tasks;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Storage;

/// <summary>
/// Persistence contract for the MCP tasks extension's durable store.
/// </summary>
public sealed class SqliteTaskStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdqa-tasks-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteMetadataStore _store;
    private readonly StubProfileAccessor _profile = new();

    public SqliteTaskStoreTests()
    {
        Directory.CreateDirectory(_dir);
        var options = new McpDatabaseQueryAppOptions { MetadataDbPath = Path.Combine(_dir, "meta.db") };
        _store = new SqliteMetadataStore(options, _profile);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static McpTaskRecord NewRecord(
        string taskId,
        string profileId = "default",
        McpTaskState state = McpTaskState.Working,
        DateTimeOffset? expiresAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpTaskRecord(
            taskId,
            profileId,
            state,
            StatusMessage: null,
            CreatedAt: now,
            LastUpdatedAt: now,
            ExpiresAt: expiresAt ?? now.AddHours(1),
            PollIntervalMs: 1000,
            ResultJson: null,
            ErrorJson: null,
            InputRequestsJson: null);
    }

    [Fact]
    public async Task Round_trips_a_task()
    {
        await _store.InsertTaskAsync(NewRecord("t1"), CancellationToken.None);

        var loaded = await _store.GetTaskAsync("t1", "default", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.TaskId.Should().Be("t1");
        loaded.State.Should().Be(McpTaskState.Working);
        loaded.PollIntervalMs.Should().Be(1000);
    }

    [Fact]
    public async Task Update_persists_result_and_terminal_state()
    {
        await _store.InsertTaskAsync(NewRecord("t2"), CancellationToken.None);

        var updated = await _store.UpdateTaskAsync(
            NewRecord("t2") with { State = McpTaskState.Completed, ResultJson = """{"rows":3}""" },
            CancellationToken.None);

        updated.Should().BeTrue();
        var loaded = await _store.GetTaskAsync("t2", "default", CancellationToken.None);
        loaded!.State.Should().Be(McpTaskState.Completed);
        loaded.ResultJson.Should().Be("""{"rows":3}""");
    }

    [Fact]
    public async Task Update_of_a_missing_task_reports_false()
    {
        var updated = await _store.UpdateTaskAsync(NewRecord("ghost"), CancellationToken.None);
        updated.Should().BeFalse();
    }

    [Fact]
    public async Task A_task_cannot_be_read_from_another_profile()
    {
        await _store.InsertTaskAsync(NewRecord("t3", profileId: "alice"), CancellationToken.None);

        // The handle is valid, but it belongs to another profile.
        var asBob = await _store.GetTaskAsync("t3", "bob", CancellationToken.None);
        asBob.Should().BeNull();

        var asAlice = await _store.GetTaskAsync("t3", "alice", CancellationToken.None);
        asAlice.Should().NotBeNull();
    }

    [Fact]
    public async Task A_null_profile_bypasses_the_filter_for_internal_callers()
    {
        // The background completion path has no ambient profile and addresses the row by id.
        await _store.InsertTaskAsync(NewRecord("t4", profileId: "alice"), CancellationToken.None);

        var loaded = await _store.GetTaskAsync("t4", null, CancellationToken.None);
        loaded.Should().NotBeNull();
    }

    [Fact]
    public async Task Purge_removes_only_expired_tasks()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertTaskAsync(NewRecord("fresh", expiresAt: now.AddHours(1)), CancellationToken.None);
        await _store.InsertTaskAsync(NewRecord("stale", expiresAt: now.AddMinutes(-1)), CancellationToken.None);

        var purged = await _store.PurgeExpiredTasksAsync(now, CancellationToken.None);

        purged.Should().Be(1);
        (await _store.GetTaskAsync("stale", "default", CancellationToken.None)).Should().BeNull();
        (await _store.GetTaskAsync("fresh", "default", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Interrupted_tasks_are_failed_but_terminal_ones_are_untouched()
    {
        await _store.InsertTaskAsync(NewRecord("running", state: McpTaskState.Working), CancellationToken.None);
        await _store.InsertTaskAsync(NewRecord("waiting", state: McpTaskState.InputRequired), CancellationToken.None);
        await _store.InsertTaskAsync(NewRecord("done", state: McpTaskState.Completed), CancellationToken.None);

        var failed = await _store.FailInterruptedTasksAsync("restarted", DateTimeOffset.UtcNow, CancellationToken.None);

        failed.Should().Be(2);
        (await _store.GetTaskAsync("running", "default", CancellationToken.None))!.State.Should().Be(McpTaskState.Failed);
        (await _store.GetTaskAsync("waiting", "default", CancellationToken.None))!.State.Should().Be(McpTaskState.Failed);

        // A finished task must keep its outcome — reconciliation must not rewrite history.
        (await _store.GetTaskAsync("done", "default", CancellationToken.None))!.State.Should().Be(McpTaskState.Completed);
    }

    private sealed class StubProfileAccessor : IProfileContextAccessor
    {
        public Profile? Current => null;

        public IProfileScope Begin(Profile profile) => throw new NotSupportedException();
    }
}
