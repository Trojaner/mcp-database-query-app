using System.Text.Json.Serialization;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Server.Tools;

namespace McpDatabaseQueryApp.Server;

// Source-generated JSON metadata for every MCP tool parameter and return type,
// plus the Core DTOs they transitively reference. Prepended to the MCP SDK's
// TypeInfoResolverChain so AOT builds can serialize these shapes without
// reflection.
//
// Not covered by this context (still relies on reflection fallback during JIT,
// needs custom handling for AOT):
// - IReadOnlyList<IReadOnlyList<object?>> in QueryToolResult/QueryPageResult —
//   the object? leaves hold arbitrary provider-returned scalars (DateTime,
//   decimal, string, Guid, byte[], ...). AOT-safe handling will require either
//   a custom converter or a typed column-value discriminator.

// Request / parameter types.
[JsonSerializable(typeof(ConnectArgs))]
[JsonSerializable(typeof(NoteSetArgs))]
[JsonSerializable(typeof(PredefinedDbArgs))]
[JsonSerializable(typeof(PredefinedDbUpdateArgs))]
[JsonSerializable(typeof(QueryToolArgs))]
[JsonSerializable(typeof(ExecuteArgs))]
[JsonSerializable(typeof(ExplainArgs))]
[JsonSerializable(typeof(ScriptArgs))]

// Tool / resource return types.
[JsonSerializable(typeof(ListPredefinedResult))]
[JsonSerializable(typeof(ListConnectionsResult))]
[JsonSerializable(typeof(ConnectResult))]
[JsonSerializable(typeof(DisconnectResult))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(NotePageResult))]
[JsonSerializable(typeof(NoteDeleteResult))]
[JsonSerializable(typeof(DeleteResult))]
[JsonSerializable(typeof(QueryToolResult))]
[JsonSerializable(typeof(QueryPageResult))]
[JsonSerializable(typeof(ExecuteResult))]
[JsonSerializable(typeof(ExplainToolResult))]
[JsonSerializable(typeof(TablePageResult))]
[JsonSerializable(typeof(TableDescribeResult))]
[JsonSerializable(typeof(BatchDescribeResult))]
[JsonSerializable(typeof(ScriptPageResult))]
[JsonSerializable(typeof(ScriptRunResult))]
[JsonSerializable(typeof(OpenUiResult))]
[JsonSerializable(typeof(UiCsvResult))]

// Core DTOs referenced by the results above.
[JsonSerializable(typeof(RedactedDescriptor))]
[JsonSerializable(typeof(NoteRecord))]
[JsonSerializable(typeof(ScriptRecord))]
[JsonSerializable(typeof(ScriptParameter))]
[JsonSerializable(typeof(SchemaInfo))]
[JsonSerializable(typeof(TableInfo))]
[JsonSerializable(typeof(ColumnInfo))]
[JsonSerializable(typeof(IndexInfo))]
[JsonSerializable(typeof(ForeignKeyInfo))]
[JsonSerializable(typeof(TableDetails))]
[JsonSerializable(typeof(RoleInfo))]
[JsonSerializable(typeof(DatabaseInfo))]
[JsonSerializable(typeof(PageRequest))]
[JsonSerializable(typeof(QueryColumn))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(NonQueryRequest))]
[JsonSerializable(typeof(QueryResult))]
[JsonSerializable(typeof(ExplainResult))]

// Common collection shapes that appear in result properties.
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlySet<string>))]
internal sealed partial class McpDatabaseQueryAppJsonContext : JsonSerializerContext;
