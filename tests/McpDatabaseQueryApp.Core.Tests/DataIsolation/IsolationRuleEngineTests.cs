using FluentAssertions;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Core.Tests.QueryExecution;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.DataIsolation;

public sealed class IsolationRuleEngineTests
{
    private static IsolationRule MakeRule(
        string id,
        string profile,
        string host,
        int port,
        string db,
        string schema,
        string table,
        IsolationFilter filter,
        IsolationRuleSource source = IsolationRuleSource.Static,
        int priority = 0)
    {
        return new IsolationRule(
            new IsolationRuleId(id),
            new ProfileId(profile),
            new IsolationScope(host, port, db, schema, table),
            filter,
            source,
            priority,
            Description: null);
    }

    private static QueryExecutionContext MakeContext(
        ParsedBatch parsed,
        Profile? profile = null,
        string host = "db.example.com",
        int port = 5432,
        string database = "analytics")
    {
        var connection = new ScopedFakeConnection("c1", DatabaseKind.Postgres, host, port, database);
        var ctx = new QueryExecutionContext(
            "SELECT 1",
            null,
            connection,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false,
            profile);
        ctx.Parsed = parsed;
        return ctx;
    }

    private sealed class ScopedFakeConnection : Core.Providers.IDatabaseConnection
    {
        public ScopedFakeConnection(string id, DatabaseKind kind, string host, int port, string database)
        {
            Id = id;
            Kind = kind;
            IsReadOnly = false;
            Descriptor = new ConnectionDescriptor
            {
                Id = id,
                Name = id,
                Provider = kind,
                Host = host,
                Port = port,
                Database = database,
                Username = "test",
                SslMode = "Disable",
            };
        }
        public string Id { get; }
        public DatabaseKind Kind { get; }
        public ConnectionDescriptor Descriptor { get; }
        public bool IsReadOnly { get; }
        public Task<Core.Providers.QueryResult> ExecuteQueryAsync(Core.Providers.QueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> ExecuteNonQueryAsync(Core.Providers.NonQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Core.Providers.SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(IReadOnlyList<Core.Providers.TableInfo> Items, long Total)> ListTablesAsync(string? schema, Core.Providers.PageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Core.Providers.TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Core.Providers.RoleInfo>> ListRolesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Core.Providers.DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Core.Providers.ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ParsedBatch SelectFrom(params string[] tables)
    {
        // Build a batch with one statement that "reads" each given table.
        var actions = new List<ParsedQueryAction>();
        foreach (var raw in tables)
        {
            string? schema = null;
            var name = raw;
            if (raw.Contains('.', StringComparison.Ordinal))
            {
                var parts = raw.Split('.', 2);
                schema = parts[0];
                name = parts[1];
            }
            var target = new ObjectReference(ObjectKind.Table, null, null, schema, name);
            actions.Add(new ParsedQueryAction(ActionKind.Read, target, []));
        }

        var stmt = new ParsedStatement(
            StatementKind.Select,
            isMutation: false,
            isDestructive: false,
            originalText: "SELECT 1",
            range: new SourceRange(0, 8),
            actions: actions,
            warnings: [],
            providerAst: ProviderAst.None);
        return new ParsedBatch(DatabaseKind.Postgres, "SELECT 1", [stmt], errors: [], providerAst: ProviderAst.None);
    }

    private sealed class FakeStore : IIsolationRuleStore
    {
        private readonly List<IsolationRule> _rules;
        public FakeStore(IEnumerable<IsolationRule> rules) { _rules = rules.ToList(); }
        public Task<IReadOnlyList<IsolationRule>> ListAsync(ProfileId profileId, ConnectionDescriptor connection, CancellationToken cancellationToken)
        {
            // Mirror SqliteIsolationRuleStore semantics: filter by profile + connection scope.
            var hits = _rules
                .Where(r => r.ProfileId.Value == profileId.Value)
                .Where(r => r.Scope.MatchesConnection(connection))
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.Id.Value, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult<IReadOnlyList<IsolationRule>>(hits);
        }
        public Task<IsolationRule?> GetAsync(IsolationRuleId id, CancellationToken cancellationToken)
            => Task.FromResult(_rules.FirstOrDefault(r => r.Id.Value == id.Value));
        public Task<IsolationRule> UpsertAsync(IsolationRule rule, CancellationToken cancellationToken)
        {
            _rules.RemoveAll(r => r.Id.Value == rule.Id.Value);
            _rules.Add(rule);
            return Task.FromResult(rule);
        }
        public Task<bool> DeleteAsync(IsolationRuleId id, CancellationToken cancellationToken)
            => Task.FromResult(_rules.RemoveAll(r => r.Id.Value == id.Value) > 0);
    }

    private static Profile DefaultProfile(string id = "default") => new(
        new ProfileId(id),
        Name: id,
        Subject: id,
        Issuer: null,
        CreatedAt: DateTimeOffset.UtcNow,
        Status: ProfileStatus.Active,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public async Task Builds_one_directive_per_matching_rule_and_skips_unrelated_tables()
    {
        var parsed = SelectFrom("public.events", "public.audits");
        var rule = MakeRule(
            "r1",
            "default",
            host: "db.example.com", port: 5432, db: "analytics",
            schema: "public",
            table: "events",
            new IsolationFilter.EqualityFilter("tenant_id", 42));

        var engine = new IsolationRuleEngine(new FakeStore(new[] { rule }));
        var ctx = MakeContext(parsed, DefaultProfile());

        var directives = await engine.BuildDirectivesAsync(ctx, CancellationToken.None);

        directives.Should().ContainSingle();
        var p = directives[0].Should().BeOfType<PredicateInjectionDirective>().Subject;
        p.Target.Schema.Should().Be("public");
        p.Target.Name.Should().Be("events");
        p.Predicate.Should().Be("tenant_id = @iso_0");

        ctx.Items.Should().ContainKey(IsolationRuleEngine.ParametersItemKey);
        var parameters = (IReadOnlyDictionary<string, object?>)ctx.Items[IsolationRuleEngine.ParametersItemKey]!;
        parameters["iso_0"].Should().Be(42);
    }

    [Fact]
    public async Task Rules_from_another_profile_do_not_apply()
    {
        var parsed = SelectFrom("public.events");
        var rule = MakeRule(
            "r1",
            "tenantA",
            host: "db.example.com", port: 5432, db: "analytics",
            schema: "public",
            table: "events",
            new IsolationFilter.EqualityFilter("tenant_id", 1));

        var engine = new IsolationRuleEngine(new FakeStore(new[] { rule }));
        var ctx = MakeContext(parsed, DefaultProfile("tenantB"));

        var directives = await engine.BuildDirectivesAsync(ctx, CancellationToken.None);

        directives.Should().BeEmpty();
    }

    [Fact]
    public async Task Rules_from_other_host_do_not_apply()
    {
        var parsed = SelectFrom("public.events");
        var rule = MakeRule(
            "r1",
            "default",
            host: "other-host", port: 5432, db: "analytics",
            schema: "public",
            table: "events",
            new IsolationFilter.EqualityFilter("tenant_id", 1));

        var engine = new IsolationRuleEngine(new FakeStore(new[] { rule }));
        var ctx = MakeContext(parsed, DefaultProfile());

        var directives = await engine.BuildDirectivesAsync(ctx, CancellationToken.None);

        directives.Should().BeEmpty();
    }

    [Fact]
    public async Task Static_and_dynamic_rules_combine_and_sort_by_priority()
    {
        var parsed = SelectFrom("public.events");
        var staticRule = MakeRule(
            "static-1",
            "default",
            host: "db.example.com", port: 5432, db: "analytics",
            schema: "public",
            table: "events",
            new IsolationFilter.EqualityFilter("tenant_id", 1),
            IsolationRuleSource.Static,
            priority: 100);
        var dynamicRule = MakeRule(
            "dynamic-1",
            "default",
            host: "db.example.com", port: 5432, db: "analytics",
            schema: "public",
            table: "events",
            new IsolationFilter.EqualityFilter("region", "us"),
            IsolationRuleSource.Dynamic,
            priority: 50);

        // The fake store returns them sorted by priority descending. The
        // engine preserves that order in the produced directives.
        var engine = new IsolationRuleEngine(new FakeStore(new[] { dynamicRule, staticRule }));
        var ctx = MakeContext(parsed, DefaultProfile());

        var directives = await engine.BuildDirectivesAsync(ctx, CancellationToken.None);

        directives.Should().HaveCount(2);
        directives.Cast<PredicateInjectionDirective>().Select(d => d.Predicate).Should().BeEquivalentTo(
            new[] { "tenant_id = @iso_0", "region = @iso_1" },
            o => o.WithStrictOrdering());
    }
}
