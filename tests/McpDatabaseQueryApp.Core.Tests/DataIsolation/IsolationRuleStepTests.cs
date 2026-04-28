using FluentAssertions;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Core.Tests.QueryExecution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.DataIsolation;

public sealed class IsolationRuleStepTests
{
    private sealed class FakeEngine : IIsolationRuleEngine
    {
        private readonly IReadOnlyList<RewriteDirective> _directives;
        public FakeEngine(IReadOnlyList<RewriteDirective> directives) { _directives = directives; }
        public Task<IReadOnlyList<RewriteDirective>> BuildDirectivesAsync(QueryExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(_directives);
    }

    private sealed class FakeRewriter : IQueryRewriter
    {
        private readonly Func<ParsedBatch, IReadOnlyList<RewriteDirective>, string> _impl;
        public FakeRewriter(DatabaseKind kind, Func<ParsedBatch, IReadOnlyList<RewriteDirective>, string> impl)
        {
            Kind = kind;
            _impl = impl;
        }
        public DatabaseKind Kind { get; }
        public string Rewrite(ParsedBatch parsed, IReadOnlyList<RewriteDirective> directives) => _impl(parsed, directives);
    }

    private sealed class FakeRewriterFactory : IQueryRewriterFactory
    {
        private readonly Dictionary<DatabaseKind, IQueryRewriter> _map = new();
        public FakeRewriterFactory Add(IQueryRewriter rewriter) { _map[rewriter.Kind] = rewriter; return this; }
        public IQueryRewriter GetRewriter(DatabaseKind kind) => _map[kind];
        public bool TryGetRewriter(DatabaseKind kind, out IQueryRewriter rewriter)
        {
            if (_map.TryGetValue(kind, out var found)) { rewriter = found; return true; }
            rewriter = null!;
            return false;
        }
    }

    private static QueryExecutionContext MakeContext(ParsedBatch parsed, string sql)
    {
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, isReadOnly: false);
        var ctx = new QueryExecutionContext(
            sql,
            null,
            conn,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);
        ctx.Parsed = parsed;
        return ctx;
    }

    [Fact]
    public async Task When_no_directives_step_does_not_invoke_rewriter_or_reparse()
    {
        var batch = TestFakes.Batch(DatabaseKind.Postgres, "SELECT 1");
        var ctx = MakeContext(batch, "SELECT 1");
        var rewriterCalls = 0;
        var parserCalls = 0;
        var rewriters = new FakeRewriterFactory().Add(new FakeRewriter(DatabaseKind.Postgres, (_, _) =>
        {
            rewriterCalls++;
            return "SELECT 99";
        }));
        var parsers = new TestFakes.FakeParserFactory().Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ =>
        {
            parserCalls++;
            return batch;
        }));
        var step = new IsolationRuleStep(
            new FakeEngine(Array.Empty<RewriteDirective>()),
            rewriters,
            parsers,
            NullLogger<IsolationRuleStep>.Instance);

        var nextCalled = false;
        await step.ExecuteAsync(ctx, ct => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        nextCalled.Should().BeTrue();
        rewriterCalls.Should().Be(0);
        parserCalls.Should().Be(0);
        ctx.Sql.Should().Be("SELECT 1");
        ctx.Parsed.Should().BeSameAs(batch);
        ctx.Items.Should().NotContainKey(IsolationRuleStep.AppliedDirectivesItemKey);
    }

    [Fact]
    public async Task When_directives_present_step_rewrites_sql_and_replaces_parsed_batch()
    {
        var original = TestFakes.Batch(DatabaseKind.Postgres, "SELECT * FROM events");
        var rewritten = TestFakes.Batch(DatabaseKind.Postgres, "SELECT * FROM events WHERE tenant_id = 1");
        var ctx = MakeContext(original, "SELECT * FROM events");

        var directives = new RewriteDirective[]
        {
            new PredicateInjectionDirective(
                new ObjectReference(ObjectKind.Table, null, null, "public", "events"),
                "tenant_id = 1"),
        };

        var rewriters = new FakeRewriterFactory().Add(new FakeRewriter(DatabaseKind.Postgres, (_, _) =>
            "SELECT * FROM events WHERE tenant_id = 1"));
        var parsers = new TestFakes.FakeParserFactory().Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ => rewritten));

        var step = new IsolationRuleStep(
            new FakeEngine(directives),
            rewriters,
            parsers,
            NullLogger<IsolationRuleStep>.Instance);

        await step.ExecuteAsync(ctx, ct => Task.CompletedTask, CancellationToken.None);

        ctx.Sql.Should().Be("SELECT * FROM events WHERE tenant_id = 1");
        ctx.Parsed.Should().BeSameAs(rewritten);
        ctx.Items[IsolationRuleStep.AppliedDirectivesItemKey].Should().BeEquivalentTo(directives);
    }

    [Fact]
    public async Task When_rewriter_throws_step_raises_IsolationRewriteFailedException()
    {
        var batch = TestFakes.Batch(DatabaseKind.Postgres, "SELECT * FROM events");
        var ctx = MakeContext(batch, "SELECT * FROM events");

        var directives = new RewriteDirective[]
        {
            new PredicateInjectionDirective(
                new ObjectReference(ObjectKind.Table, null, null, "public", "events"),
                "tenant_id = 1"),
        };

        var rewriters = new FakeRewriterFactory().Add(new FakeRewriter(DatabaseKind.Postgres, (_, _) =>
            throw new QueryParseException("boom")));
        var parsers = new TestFakes.FakeParserFactory().Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ => batch));

        var step = new IsolationRuleStep(
            new FakeEngine(directives),
            rewriters,
            parsers,
            NullLogger<IsolationRuleStep>.Instance);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<IsolationRewriteFailedException>();
    }

    [Fact]
    public async Task When_no_rewriter_registered_for_dialect_step_fails_closed()
    {
        var batch = TestFakes.Batch(DatabaseKind.Postgres, "SELECT * FROM events");
        var ctx = MakeContext(batch, "SELECT * FROM events");

        var directives = new RewriteDirective[]
        {
            new PredicateInjectionDirective(
                new ObjectReference(ObjectKind.Table, null, null, "public", "events"),
                "tenant_id = 1"),
        };

        var step = new IsolationRuleStep(
            new FakeEngine(directives),
            new FakeRewriterFactory(),
            new TestFakes.FakeParserFactory(),
            NullLogger<IsolationRuleStep>.Instance);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<IsolationRewriteFailedException>();
    }

    [Fact]
    public async Task Step_is_idempotent_when_no_rules_apply_after_first_invocation()
    {
        var batch = TestFakes.Batch(DatabaseKind.Postgres, "SELECT 1");
        var ctx = MakeContext(batch, "SELECT 1");
        var step = new IsolationRuleStep(
            new FakeEngine(Array.Empty<RewriteDirective>()),
            new FakeRewriterFactory(),
            new TestFakes.FakeParserFactory(),
            NullLogger<IsolationRuleStep>.Instance);

        await step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        await step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);

        ctx.Sql.Should().Be("SELECT 1");
        ctx.Parsed.Should().BeSameAs(batch);
    }
}
