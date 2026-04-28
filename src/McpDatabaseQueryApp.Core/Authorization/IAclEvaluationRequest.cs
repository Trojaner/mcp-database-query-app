using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Inputs to a single ACL check. The pipeline produces one
/// <see cref="IAclEvaluationRequest"/> per <see cref="ParsedQueryAction"/> in
/// the batch (and one per touched column for column-level checks).
/// </summary>
public interface IAclEvaluationRequest
{
    /// <summary>The profile under which the query runs.</summary>
    ProfileId Profile { get; }

    /// <summary>
    /// Descriptor of the connection the query targets. The evaluator pulls
    /// host/port/database from this descriptor.
    /// </summary>
    ConnectionDescriptor ConnectionTarget { get; }

    /// <summary>The object the action references (table, view, column).</summary>
    ObjectReference Object { get; }

    /// <summary>
    /// Optional column the action touches. <c>null</c> for table-level
    /// actions (DROP TABLE, INSERT into the table as a whole, etc.).
    /// </summary>
    string? Column { get; }

    /// <summary>The single operation being checked.</summary>
    AclOperation Operation { get; }
}

/// <summary>
/// Default <see cref="IAclEvaluationRequest"/> implementation.
/// </summary>
public sealed record AclEvaluationRequest(
    ProfileId Profile,
    ConnectionDescriptor ConnectionTarget,
    ObjectReference Object,
    AclOperation Operation,
    string? Column = null) : IAclEvaluationRequest;
