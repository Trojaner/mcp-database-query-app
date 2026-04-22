using McpDatabaseQueryApp.Core.Scripts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Prompts;

public sealed class ScriptPromptProvider
{
    private readonly IScriptStore _scripts;

    public ScriptPromptProvider(IScriptStore scripts)
    {
        _scripts = scripts;
    }

    public async Task<ListPromptsResult> ListAsync(CancellationToken cancellationToken)
    {
        var (items, _) = await _scripts.ListAsync(0, 200, null, cancellationToken).ConfigureAwait(false);
        var prompts = new List<Prompt>(StaticPrompts);
        prompts.AddRange(items.Select(script => new Prompt
        {
            Name = $"script:{script.Name}",
            Description = script.Description ?? $"Run saved query: {script.Name}",
            Arguments = BuildArguments(script),
        }));

        return new ListPromptsResult { Prompts = prompts };
    }

    public async Task<GetPromptResult> GetAsync(string name, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        if (!IsScriptPrompt(name))
        {
            return GetStaticPrompt(name, arguments);
        }

        var scriptName = name["script:".Length..];
        var script = await _scripts.GetAsync(scriptName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Script prompt '{scriptName}' not found.");

        var sql = SubstituteParameters(script.SqlText, script.Parameters, arguments);
        var notesSection = string.IsNullOrWhiteSpace(script.Notes) ? string.Empty : $"\n\nNotes: {script.Notes}";
        var text = $"Execute the following SQL query using db_execute or db_query:\n\n```sql\n{sql}\n```{notesSection}\n\n"
            + "Use the appropriate connection and confirm execution if prompted.";

        return new GetPromptResult
        {
            Description = script.Description ?? $"Saved query: {script.Name}",
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock { Text = text },
                },
            ],
        };
    }

    public bool IsScriptPrompt(string name) => name.StartsWith("script:", StringComparison.Ordinal);

    private static GetPromptResult GetStaticPrompt(string name, IReadOnlyDictionary<string, string>? arguments)
    {
        string Arg(string key) => arguments?.TryGetValue(key, out var v) == true ? v : $"{{{key}}}";

        var text = name switch
        {
            "explore-database" =>
                $"You are about to explore the database identified by connection '{Arg("connectionId")}'. "
                + "Start by listing schemas via db_schemas_list, then pick a schema and call db_tables_list, "
                + "and finally use db_table_describe to explain each table's columns, indexes, and foreign keys. "
                + "Only run SELECT queries — this prompt is read-only.",
            "safe-write" =>
                $"Run the following SQL safely on connection '{Arg("connectionId")}':\n\n{Arg("sql")}\n\n"
                + "Before executing, call db_explain to show the plan, then call db_execute with confirm=false "
                + "so the user is asked to confirm via elicitation. Only proceed if the user accepts.",
            "migrate-script" =>
                $"On connection '{Arg("connectionId")}', draft a forward + reverse migration for: {Arg("goal")}. "
                + "First call db_table_describe on any touched tables, then produce idempotent SQL, "
                + "and finally suggest saving it via scripts_create.",
            "explain-slow-query" =>
                $"Explain why the following query might be slow on connection '{Arg("connectionId")}':\n\n{Arg("sql")}\n\n"
                + "Call db_explain, then walk the user through the plan and suggest index or rewrite candidates.",
            _ => throw new KeyNotFoundException($"Prompt '{name}' not found."),
        };

        return new GetPromptResult
        {
            Description = StaticPrompts.FirstOrDefault(p => p.Name == name)?.Description ?? name,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        };
    }

    private static List<PromptArgument> BuildArguments(ScriptRecord script)
    {
        var args = new List<PromptArgument>
        {
            new()
            {
                Name = "connectionId",
                Description = "Connection ID to execute the query against.",
                Required = true,
            },
        };

        foreach (var param in script.Parameters)
        {
            args.Add(new PromptArgument
            {
                Name = param.Name,
                Description = param.Description ?? $"Parameter: {param.Name}",
                Required = param.Required,
            });
        }

        return args;
    }

    private static string SubstituteParameters(
        string sql,
        IReadOnlyList<ScriptParameter> parameters,
        IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments is null || parameters.Count == 0)
        {
            return sql;
        }

        var result = sql;
        foreach (var param in parameters)
        {
            if (arguments.TryGetValue(param.Name, out var value))
            {
                result = result.Replace($"@{param.Name}", $"'{value}'", StringComparison.OrdinalIgnoreCase);
            }
            else if (param.DefaultValue is not null)
            {
                result = result.Replace($"@{param.Name}", $"'{param.DefaultValue}'", StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    private static readonly List<Prompt> StaticPrompts =
    [
        new()
        {
            Name = "explore-database",
            Description = "Guides the model through introspecting a connected database.",
            Arguments = [new() { Name = "connectionId", Description = "Opaque connection id returned by db_connect.", Required = true }],
        },
        new()
        {
            Name = "safe-write",
            Description = "Wraps a destructive operation in a confirmation + explain dry run.",
            Arguments =
            [
                new() { Name = "connectionId", Description = "Opaque connection id returned by db_connect.", Required = true },
                new() { Name = "sql", Description = "The destructive SQL statement to wrap.", Required = true },
            ],
        },
        new()
        {
            Name = "migrate-script",
            Description = "Drafts a migration script for a target schema change.",
            Arguments =
            [
                new() { Name = "connectionId", Description = "Opaque connection id returned by db_connect.", Required = true },
                new() { Name = "goal", Description = "Plain-English description of the desired schema change.", Required = true },
            ],
        },
        new()
        {
            Name = "explain-slow-query",
            Description = "Explains a slow query using the provider's native plan format.",
            Arguments =
            [
                new() { Name = "connectionId", Description = "Opaque connection id returned by db_connect.", Required = true },
                new() { Name = "sql", Description = "The SQL to explain.", Required = true },
            ],
        },
    ];
}
