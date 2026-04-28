using FluentAssertions;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryParsing;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Authorization;

public sealed class AclEvaluatorTests
{
    private static readonly ProfileId ProfileA = new("p_a");
    private static readonly ProfileId ProfileB = new("p_b");

    [Fact]
    public async Task DefaultProfile_with_AllowAll_short_circuits()
    {
        var store = new InMemoryStore();
        var evaluator = new AclEvaluator(store, () => new AuthorizationOptions
        {
            DefaultProfilePolicy = DefaultProfilePolicy.AllowAll,
        });

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileId.Default, "users", AclOperation.Read), CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
        decision.MatchingEntry.Should().BeNull();
        decision.Reason.Should().Contain("default profile bypass");
    }

    [Fact]
    public async Task DefaultProfile_with_DenyAll_evaluates_normally()
    {
        var store = new InMemoryStore();
        var evaluator = new AclEvaluator(store, () => new AuthorizationOptions
        {
            DefaultProfilePolicy = DefaultProfilePolicy.DenyAll,
        });

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileId.Default, "users", AclOperation.Read), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
        decision.Reason.Should().Contain("no matching ACL entry");
    }

    [Fact]
    public async Task NonDefault_profile_with_no_entries_is_default_deny()
    {
        var store = new InMemoryStore();
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Wildcard_allow_matches_request()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read), CancellationToken.None);
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Exact_table_allow_matches_only_that_table()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read,
            new AclObjectScope(null, null, null, "public", "users", null)));
        var evaluator = NewEvaluator(store);

        (await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read, schema: "public"), CancellationToken.None))
            .IsAllowed.Should().BeTrue();

        (await evaluator.EvaluateAsync(MakeRequest(ProfileA, "orders", AclOperation.Read, schema: "public"), CancellationToken.None))
            .Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Operation_outside_allowed_set_does_not_match()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Insert), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Higher_priority_wins_over_lower_priority()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any, priority: 0));
        store.Add(Entry(ProfileA, AclEffect.Deny, AclOperation.Read,
            new AclObjectScope(null, null, null, null, "secrets", null),
            priority: 100));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "secrets", AclOperation.Read), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task DenyWins_at_equal_priority()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any, priority: 50));
        store.Add(Entry(ProfileA, AclEffect.Deny, AclOperation.Read, AclObjectScope.Any, priority: 50));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Column_scoped_entry_does_not_match_table_level_request()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read,
            new AclObjectScope(null, null, null, "public", "users", "email")));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Column_scoped_deny_blocks_specific_column()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any, priority: 10));
        store.Add(Entry(ProfileA, AclEffect.Deny, AclOperation.Read,
            new AclObjectScope(null, null, null, null, "users", "ssn"),
            priority: 100));
        var evaluator = NewEvaluator(store);

        var ssnReq = MakeRequest(ProfileA, "users", AclOperation.Read, column: "ssn");
        (await evaluator.EvaluateAsync(ssnReq, CancellationToken.None))
            .Effect.Should().Be(AclEffect.Deny);

        var emailReq = MakeRequest(ProfileA, "users", AclOperation.Read, column: "email");
        (await evaluator.EvaluateAsync(emailReq, CancellationToken.None))
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Cross_profile_entries_do_not_leak()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileB, "users", AclOperation.Read), CancellationToken.None);
        decision.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Connection_target_filtering_matches_host_port_db()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read,
            new AclObjectScope("prod-db.internal", 5432, "analytics", null, null, null)));
        var evaluator = NewEvaluator(store);

        var matchingDescriptor = new ConnectionDescriptor
        {
            Id = "c", Name = "c", Provider = DatabaseKind.Postgres,
            Host = "prod-db.internal", Port = 5432, Database = "analytics",
            Username = "u", SslMode = "Require",
        };
        var nonMatchingDescriptor = matchingDescriptor with { Database = "staging" };

        (await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read, descriptor: matchingDescriptor), CancellationToken.None))
            .IsAllowed.Should().BeTrue();

        (await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read, descriptor: nonMatchingDescriptor), CancellationToken.None))
            .Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Static_source_entries_apply_with_priority_offset()
    {
        var store = new InMemoryStore();
        var staticSource = new InMemoryStaticSource();
        staticSource.Add(ProfileA, Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any, priority: 5));

        var evaluator = new AclEvaluator(store, () => new AuthorizationOptions(), staticSource);
        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read), CancellationToken.None);
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Match_uses_case_insensitive_equality_for_strings()
    {
        var store = new InMemoryStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read,
            new AclObjectScope(null, null, null, "PUBLIC", "Users", null)));
        var evaluator = NewEvaluator(store);

        var decision = await evaluator.EvaluateAsync(MakeRequest(ProfileA, "users", AclOperation.Read, schema: "public"), CancellationToken.None);
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Cache_serves_repeated_lookups_within_ttl()
    {
        var store = new CountingStore();
        store.Add(Entry(ProfileA, AclEffect.Allow, AclOperation.Read, AclObjectScope.Any));
        var evaluator = new AclEvaluator(store, () => new AuthorizationOptions
        {
            CacheTtl = TimeSpan.FromMinutes(5),
        });

        await evaluator.EvaluateAsync(MakeRequest(ProfileA, "a", AclOperation.Read), CancellationToken.None);
        await evaluator.EvaluateAsync(MakeRequest(ProfileA, "b", AclOperation.Read), CancellationToken.None);
        await evaluator.EvaluateAsync(MakeRequest(ProfileA, "c", AclOperation.Read), CancellationToken.None);

        store.ListCalls.Should().Be(1);

        evaluator.Invalidate(ProfileA);
        await evaluator.EvaluateAsync(MakeRequest(ProfileA, "d", AclOperation.Read), CancellationToken.None);
        store.ListCalls.Should().Be(2);
    }

    private static AclEvaluator NewEvaluator(IAclStore store)
        => new(store, () => new AuthorizationOptions
        {
            DefaultProfilePolicy = DefaultProfilePolicy.DenyAll,
        });

    private static AclEntry Entry(
        ProfileId profile,
        AclEffect effect,
        AclOperation operations,
        AclObjectScope scope,
        int priority = 0,
        string? description = null)
    {
        return new AclEntry(
            AclEntryId.NewId(),
            profile,
            AclSubjectKind.Profile,
            scope,
            operations,
            effect,
            priority,
            description);
    }

    private static AclEvaluationRequest MakeRequest(
        ProfileId profile,
        string table,
        AclOperation operation,
        string? schema = null,
        string? column = null,
        ConnectionDescriptor? descriptor = null)
    {
        descriptor ??= new ConnectionDescriptor
        {
            Id = "c1", Name = "c1", Provider = DatabaseKind.Postgres,
            Host = "h", Port = 5432, Database = "d", Username = "u", SslMode = "Disable",
        };
        var obj = new ObjectReference(ObjectKind.Table, Server: null, Database: null, Schema: schema, Name: table);
        return new AclEvaluationRequest(profile, descriptor, obj, operation, column);
    }

    private sealed class InMemoryStore : IAclStore
    {
        private readonly List<AclEntry> _entries = new();

        public void Add(AclEntry entry) => _entries.Add(entry);

        public Task<IReadOnlyList<AclEntry>> ListAsync(ProfileId profile, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AclEntry>>(_entries.Where(e => e.ProfileId.Value == profile.Value).ToList());

        public Task<AclEntry?> GetAsync(AclEntryId id, CancellationToken ct)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id.Value == id.Value));

        public Task<AclEntry> UpsertAsync(AclEntry entry, CancellationToken ct)
        {
            _entries.RemoveAll(e => e.Id.Value == entry.Id.Value);
            _entries.Add(entry);
            return Task.FromResult(entry);
        }

        public Task<bool> DeleteAsync(ProfileId profile, AclEntryId id, CancellationToken ct)
        {
            var removed = _entries.RemoveAll(e => e.ProfileId.Value == profile.Value && e.Id.Value == id.Value);
            return Task.FromResult(removed > 0);
        }

        public Task ReplaceAllAsync(ProfileId profile, IReadOnlyList<AclEntry> entries, CancellationToken ct)
        {
            _entries.RemoveAll(e => e.ProfileId.Value == profile.Value);
            _entries.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingStore : IAclStore
    {
        private readonly List<AclEntry> _entries = new();

        public int ListCalls { get; private set; }

        public void Add(AclEntry entry) => _entries.Add(entry);

        public Task<IReadOnlyList<AclEntry>> ListAsync(ProfileId profile, CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<AclEntry>>(_entries.Where(e => e.ProfileId.Value == profile.Value).ToList());
        }

        public Task<AclEntry?> GetAsync(AclEntryId id, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AclEntry> UpsertAsync(AclEntry entry, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(ProfileId profile, AclEntryId id, CancellationToken ct)
            => throw new NotSupportedException();

        public Task ReplaceAllAsync(ProfileId profile, IReadOnlyList<AclEntry> entries, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class InMemoryStaticSource : IAclStaticEntrySource
    {
        private readonly Dictionary<string, List<AclEntry>> _byProfile = new(StringComparer.Ordinal);

        public void Add(ProfileId profile, AclEntry entry)
        {
            if (!_byProfile.TryGetValue(profile.Value, out var list))
            {
                list = new List<AclEntry>();
                _byProfile[profile.Value] = list;
            }
            list.Add(entry);
        }

        public IReadOnlyList<AclEntry> GetEntriesFor(ProfileId profile)
            => _byProfile.TryGetValue(profile.Value, out var list) ? list : Array.Empty<AclEntry>();
    }
}
