# AGENTS.md — MCP Database Query App

Living instructions for agents contributing to this repository. Re-read this
file at the start of every session. If any rule below stops matching reality,
update this file in the same change that breaks it.

---

## 1. Mission

MCP Database Query App is a mature Model Context Protocol server written in C# on .NET 10
that lets LLM clients manage PostgreSQL and SQL Server databases through
Dapper. It exposes pre-defined and ad-hoc connections, saved SQL scripts,
schema introspection, and query execution via the full 2025-11-25 MCP
feature set (tools, resources, completions, pagination, elicitation,
logging, MCP Apps UI). Credentials must never leave the server process in
any MCP payload. Large results must always be safe to request.

## 2. Repository layout

```
src/
  McpDatabaseQueryApp.Server/             # Composition root, Program.cs, stdio + HTTP/SSE hosting
  McpDatabaseQueryApp.Core/               # Domain contracts, options, registry, result limiter, SQLite metadata store
  McpDatabaseQueryApp.Providers.Postgres/ # Npgsql + Dapper, PostgreSQL-only features
  McpDatabaseQueryApp.Providers.SqlServer/# Microsoft.Data.SqlClient + Dapper, SQL Server-only features
  McpDatabaseQueryApp.Apps/               # TypeScript UI sources + embedded HTML bundles
tests/
  McpDatabaseQueryApp.Core.Tests/
  McpDatabaseQueryApp.Providers.Postgres.Tests/   # Testcontainers.PostgreSql
  McpDatabaseQueryApp.Providers.SqlServer.Tests/  # Testcontainers.MsSql
  McpDatabaseQueryApp.Server.IntegrationTests/    # in-process MCP client end-to-end
AGENTS.md
README.md
McpDatabaseQueryApp.slnx
global.json               # pins the .NET 10 SDK
Directory.Build.props     # Nullable, ImplicitUsings, analyzers, LangVersion
Directory.Packages.props  # Central Package Management versions
.editorconfig
```

## 3. Layering rules (non-negotiable)

1. `McpDatabaseQueryApp.Core` **must not** reference the MCP SDK, ASP.NET Core, or any
   database driver. It owns domain abstractions (`IDatabaseProvider`,
   `IDatabaseConnection`, `IConnectionRegistry`, `IScriptStore`,
   `IResultLimiter`, `IMetadataStore`, `ICredentialProtector`), options,
   DTOs, and the `DatabaseKind` enum.
2. `McpDatabaseQueryApp.Providers.Postgres` and `McpDatabaseQueryApp.Providers.SqlServer`
   implement `IDatabaseProvider` + `IDatabaseConnection`. Provider-specific
   features (PostgreSQL `LISTEN`/`NOTIFY`, `EXPLAIN (ANALYZE, FORMAT JSON)`,
   `pg_stat_activity`, extensions; SQL Server `sp_who2`,
   `sys.dm_exec_requests`, Agent jobs, showplan JSON) live here, gated
   behind flags on `ProviderCapabilities`. Provider projects **must not**
   reference each other.
3. `McpDatabaseQueryApp.Server` is the **only** project that references
   `ModelContextProtocol` and `ModelContextProtocol.AspNetCore`. It owns
   tool classes, resource classes, prompt classes, the completion router,
   elicitation gateway, pagination handlers, and `McpLoggerProvider`.
4. `McpDatabaseQueryApp.Apps` ships the MCP Apps UI as embedded resources. `Server`
   references it to register `ui://` resources. UI is always an
   enhancement — every tool must remain fully functional in a client with
   no MCP Apps support.
5. Dependencies flow in one direction only:
   `Apps → Server → {Providers.Postgres, Providers.SqlServer} → Core`.
   No back-edges.

## 4. How to add a tool

1. Decide the project: generic tools go in `McpDatabaseQueryApp.Server/Tools/`;
   provider-specific tools go in
   `McpDatabaseQueryApp.Providers.<Kind>/<Kind>OnlyTools.cs`.
2. Create (or reuse) a `[McpServerToolType]` class and mark methods with
   `[McpServerTool, Description("…")]`. Prefer small, focused classes over
   a single monolithic tool file.
3. Inject dependencies through the method signature — `IMcpServer server`,
   `IConnectionRegistry`, `IScriptStore`, `IElicitationGateway`,
   `IResultLimiter`, `ILogger<T>`, `CancellationToken`.
4. Always attach an `outputSchema` when returning structured data, and
   populate `structuredContent` **and** a text content block (compact
   ASCII table or JSON) for text-mode clients.
5. If the tool has an inline UI, set `_meta.ui.resourceUri` to a
   `ui://mcp-database-query-app/...` URI via `WithUiResourceMeta(...)`. UI-only helper
   tools (`ui.*`) must set `_meta.ui.visibility = ["app"]` so the model
   cannot see them.
6. Destructive tools **must** route through
   `IElicitationGateway.ConfirmAsync(...)` unless the caller passed an
   explicit `confirm=true` argument. Read-only connections must hard-block
   writes before the elicitation path is even reached.
7. Parameterize every SQL statement. Never concatenate or interpolate
   user-supplied values into SQL strings — use Dapper parameters.
8. Update or add an integration test (see §8) that exercises the happy
   path and at least one failure path.

## 5. How to add a provider-specific feature

1. Declare the capability on `ProviderCapabilities` (new bool or list).
2. Implement it in the relevant provider project, under
   `Providers.<Kind>/<Kind>OnlyTools.cs` or a dedicated file.
3. Register the tool class through `Add<Kind>Tools()` extension methods
   that the `Server` composition root calls.
4. In `CapabilityAwareToolLister`, only surface the tool when at least
   one active connection exposes the matching capability; raise
   `notifications/tools/list_changed` when the set changes so clients
   refresh their cache.
5. The tool must degrade gracefully when no matching connection is
   active — return a tool-execution error (`isError: true`) with an
   actionable message rather than crashing.

## 6. Security checklist

Every change that touches tools, storage, or connection handling must be
checked against this list before merging.

- [ ] All SQL uses Dapper parameters — no string interpolation of user
      values into SQL.
- [ ] No code path writes a password, connection string with password, or
      any credential into an MCP payload (`content`, `structuredContent`,
      resource contents), an MCP log notification, a file on disk outside
      the SQLite store, or a standard logging sink.
- [ ] New config fields that carry credentials go through
      `ICredentialProtector` (AES-256-GCM) and live in the SQLite
      `databases` table, not in `appsettings.json`.
- [ ] `RedactedDescriptor` (or an equivalent redacting mapper) is used
      for every outbound descriptor serialization. The "no password leaks"
      integration test must still pass with the new code path covered.
- [ ] Destructive DDL/DML (DROP, TRUNCATE, DELETE/UPDATE without WHERE,
      ALTER DROP, TRUNCATE, `shutdown`, etc.) goes through
      `IElicitationGateway.ConfirmAsync` unless the caller passed
      `confirm=true`.
- [ ] `ReadOnly` connections hard-refuse any non-query path, including
      tool arguments that would hand-craft a transaction.
- [ ] SSL is required for pre-defined connections unless the entry
      explicitly opts out, and opting out logs a warning at startup.
- [ ] URL-mode elicitation URLs never contain user secrets, PII, or
      pre-authenticated tokens. Form-mode elicitation never requests a
      password, API key, or similar (the spec forbids it).

## 7. Result handling rules

1. Default row limit is 500 (`McpDatabaseQueryApp:DefaultResultLimit`). `MaxResultLimit`
   is 50 000.
2. `limit=0` (unlimited) requires **both** `AllowDisableLimit=true` **and**
   a boolean elicitation confirmation. Unconfirmed `limit=0` returns a
   tool-execution error with guidance.
3. Any result whose row count exceeds the effective limit is persisted as
   a `result_sets/{id}` record (SQLite row + spill file under
   `%APPDATA%/McpDatabaseQueryApp/cache/`) with a 10-minute idle TTL. The first page
   is returned inline with a `cursor`. Subsequent pages go through
   `db.query.next_page` or `resources/read mcpdb://result_sets/{id}?cursor=…`.
4. Cursors are opaque base64-encoded tokens produced by `CursorCodec`.
   Do not let callers synthesize cursors — always validate through the
   codec.

## 8. Testing requirements

- New tool → at least one end-to-end test in
  `tests/McpDatabaseQueryApp.Server.IntegrationTests/` that drives it through an
  in-process MCP client, covering the happy path and one failure mode.
- New provider feature → test in the matching `Providers.*.Tests` project
  using `Testcontainers.PostgreSql` (PG 17) or `Testcontainers.MsSql`
  (MSSQL 2022).
- The "no password leaks" guard test in
  `tests/McpDatabaseQueryApp.Core.Tests/CredentialRedactionTests.cs` must stay green.
  When adding new serialization paths, extend that test's payload scan
  rather than bypassing it.
- UI resources have two required checks: (a) plain HTML render with JS
  disabled still shows a usable table, (b) the MCP Apps host smoke test
  exercises the `ui.*` callback round-trip.
- Run both `dotnet build` and `dotnet test` before declaring any task
  complete. Manual smoke via `mcptools inspect` is encouraged for
  tool-surface changes.

## 9. Logging

- Use `ILogger<T>` via standard DI. `McpLoggerProvider` bridges it to MCP
  `notifications/message`, honoring the client-set level via
  `SetLoggingLevelHandler`.
- SQL execution log entries use `logger="mcp-database-query-app.sql"` and include
  `{connectionId, sql, parameters?, rows, ms}`. Literal parameter values
  are redacted unless `McpDatabaseQueryApp:Logging:RedactLiteralsInLogs=false`.
- Slow queries (configurable threshold) escalate to `warning`; connection
  errors to `error`. Never log credentials.

## 10. Configuration

- Static process config: `appsettings.json` → env vars → user secrets,
  bound to `McpDatabaseQueryAppOptions`.
- Runtime metadata (pre-defined databases, saved scripts, cached result
  sets) lives in SQLite at `%APPDATA%/McpDatabaseQueryApp/mcp-database-query-app.db`, accessed via
  Dapper through `IMetadataStore`. Schema is migrated at startup by
  `SqliteSchema` against a `_schema_version` table — never mutate the
  schema from tools.
- The master key used for AES-GCM credential protection is resolved from
  `McpDatabaseQueryApp:Secrets:KeyRef` (`UserSecrets:…`, `Env:…`, `File:…`). Never
  hard-code a key in source.

## 11. MCP feature map

| Feature     | Home                                                    |
| ----------- | ------------------------------------------------------- |
| Tools       | `McpDatabaseQueryApp.Server/Tools/*.cs`, `Providers.*/…OnlyTools.cs` |
| Resources   | `McpDatabaseQueryApp.Server/Resources/*.cs`, `McpDatabaseQueryApp.Apps/AppResources.cs` |
| Prompts     | `McpDatabaseQueryApp.Server/Prompts/*.cs`                          |
| Completions | `McpDatabaseQueryApp.Server/Completions/CompletionRouter.cs`       |
| Pagination  | `PagedResourceLister`, `CapabilityAwareToolLister`, `CursorCodec` |
| Elicitation | `McpDatabaseQueryApp.Server/Elicitation/ElicitationGateway.cs`     |
| Logging     | `McpDatabaseQueryApp.Server/Logging/McpLoggerProvider.cs`          |
| UI          | `ui://mcp-database-query-app/results.html`, `ui://mcp-database-query-app/builder.html` |

## 12. Provider-specific affordances

These are the currently sanctioned provider-only tools. Add to this list
when you introduce a new one.

PostgreSQL (`DatabaseKind.Postgres`)
- `db.pg.explain_analyze` — `EXPLAIN (ANALYZE, FORMAT JSON)` wrapper.
- `db.pg.listen` / `db.pg.notify` — async channel pub/sub bound to a
  connection id.
- `db.pg.extensions.list` — lists installed extensions via `pg_extension`.
- `db.pg.stat_activity` — snapshot of `pg_stat_activity`.

SQL Server (`DatabaseKind.SqlServer`)
- `db.mssql.sp_who2` — active sessions summary.
- `db.mssql.agent_jobs.list` — `msdb.dbo.sysjobs` enumeration.
- `db.mssql.showplan_json` — showplan JSON for a SELECT.
- `db.mssql.dm_exec_requests` — running request inspection.

## 13. Commit and documentation discipline

- Keep commits focused. A tool change, its tests, and its doc update may
  (and should) land together.
- Update `README.md` whenever user-facing config or installation changes.
- Update **this file** (`AGENTS.md`) in the same change whenever any rule
  in sections 3, 4, 5, 6, 7, 8, 9, 10, or 12 is affected. If you had to
  change the plan to ship your work, update the plan too — the plan and
  `AGENTS.md` must not drift.
- Do not add AI attribution to commits, PRs, file headers, or
  documentation. Messages read as if written by the committing engineer.
