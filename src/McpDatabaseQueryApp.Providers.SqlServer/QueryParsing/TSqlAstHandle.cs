using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;

/// <summary>
/// Wrapper hosted inside <see cref="ProviderAst.Value"/> so the SQL Server
/// rewriter can recover the underlying ScriptDom fragment without leaking
/// the type into Core.
/// </summary>
internal sealed record TSqlAstHandle(TSqlFragment Fragment, TSqlScript? Script)
{
    public const string TagName = "tsql";
}
