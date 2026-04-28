using System.Collections.Generic;

namespace McpDatabaseQueryApp.Server.AdminApi.Models;

/// <summary>Wire representation of an isolation rule.</summary>
public sealed record IsolationRuleResponse(
    string RuleId,
    string ProfileId,
    IsolationScopeDto Scope,
    IsolationFilterDto Filter,
    string Source,
    int Priority,
    string? Description,
    bool ReadOnly);

/// <summary>Wire representation of an <see cref="Core.DataIsolation.IsolationScope"/>.</summary>
public sealed record IsolationScopeDto(
    string Host,
    int Port,
    string DatabaseName,
    string Schema,
    string Table);

/// <summary>
/// Tagged-union wire representation of an
/// <see cref="Core.DataIsolation.IsolationFilter"/>. The <see cref="Kind"/>
/// discriminator selects which of the kind-specific fields are set:
/// <list type="bullet">
///   <item><description><c>Equality</c>: <see cref="Column"/> + <see cref="Value"/>.</description></item>
///   <item><description><c>InList</c>: <see cref="Column"/> + <see cref="Values"/>.</description></item>
///   <item><description><c>RawSql</c>: <see cref="Predicate"/> + <see cref="Parameters"/>.</description></item>
/// </list>
/// </summary>
public sealed record IsolationFilterDto(
    string Kind,
    string? Column,
    object? Value,
    IReadOnlyList<object?>? Values,
    string? Predicate,
    IReadOnlyDictionary<string, object?>? Parameters);

/// <summary>POST body for <c>POST .../isolation-rules</c>.</summary>
public sealed record CreateIsolationRuleRequest(
    IsolationScopeDto Scope,
    IsolationFilterDto Filter,
    int Priority,
    string? Description);

/// <summary>PATCH body for <c>PATCH .../isolation-rules/{ruleId}</c>.</summary>
public sealed record PatchIsolationRuleRequest(
    IsolationScopeDto? Scope,
    IsolationFilterDto? Filter,
    int? Priority,
    string? Description);
