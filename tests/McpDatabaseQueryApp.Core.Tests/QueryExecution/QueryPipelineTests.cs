using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryExecution;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.QueryExecution;

public sealed class QueryPipelineTests
{
    private sealed class RecordingStep : IQueryExecutionStep
    {
        private readonly List<string> _log;
        private readonly string _name;
        private readonly bool _throw;
        private readonly bool _callNext;

        public RecordingStep(int order, string name, List<string> log, bool throwException = false, bool callNext = true)
        {
            Order = order;
            _name = name;
            _log = log;
            _throw = throwException;
            _callNext = callNext;
        }

        public int Order { get; }

        public async Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken)
        {
            _log.Add(_name);
            if (_throw)
            {
                throw new InvalidOperationException($"step {_name} rejected");
            }

            if (_callNext)
            {
                await next(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static QueryExecutionContext NewContext()
    {
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, false);
        return new QueryExecutionContext("SELECT 1", null, conn, QueryExecutionMode.Read, false, false);
    }

    [Fact]
    public async Task Sorts_by_order_regardless_of_insertion()
    {
        var log = new List<string>();
        var steps = new IQueryExecutionStep[]
        {
            new RecordingStep(500, "logging", log),
            new RecordingStep(100, "parse", log),
            new RecordingStep(400, "safety", log),
        };

        var pipeline = new QueryPipeline(steps);
        await pipeline.ExecuteAsync(NewContext(), CancellationToken.None);

        log.Should().Equal("parse", "safety", "logging");
    }

    [Fact]
    public async Task Step_that_skips_next_short_circuits_remaining_steps()
    {
        var log = new List<string>();
        var steps = new IQueryExecutionStep[]
        {
            new RecordingStep(100, "parse", log),
            new RecordingStep(400, "safety", log, callNext: false),
            new RecordingStep(500, "logging", log),
        };

        var pipeline = new QueryPipeline(steps);
        await pipeline.ExecuteAsync(NewContext(), CancellationToken.None);

        log.Should().Equal("parse", "safety");
    }

    [Fact]
    public async Task Throwing_step_propagates_exception_unchanged()
    {
        var log = new List<string>();
        var steps = new IQueryExecutionStep[]
        {
            new RecordingStep(100, "parse", log),
            new RecordingStep(400, "safety", log, throwException: true),
            new RecordingStep(500, "logging", log),
        };

        var pipeline = new QueryPipeline(steps);
        var act = () => pipeline.ExecuteAsync(NewContext(), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("safety");
        log.Should().Equal("parse", "safety");
    }

    [Fact]
    public async Task Empty_pipeline_completes_successfully()
    {
        var pipeline = new QueryPipeline([]);
        await pipeline.ExecuteAsync(NewContext(), CancellationToken.None);
    }
}
