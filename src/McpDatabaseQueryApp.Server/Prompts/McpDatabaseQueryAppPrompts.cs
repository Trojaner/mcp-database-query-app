using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Prompts;

[McpServerPromptType]
public sealed class McpDatabaseQueryAppPrompts
{
    [McpServerPrompt(Name = "explore-database")]
    [Description("Guides the model through introspecting a connected database.")]
    public static PromptMessage ExploreDatabase(
        [Description("Opaque connection id returned by db_connect.")] string connectionId)
        => UserMessage(
            $"You are about to explore the database identified by connection '{connectionId}'. "
            + "Start by listing schemas via db_schemas_list, then pick a schema and call db_tables_list, "
            + "and finally use db_table_describe to explain each table's columns, indexes, and foreign keys. "
            + "Only run SELECT queries — this prompt is read-only.");

    [McpServerPrompt(Name = "safe-write")]
    [Description("Wraps a destructive operation in a confirmation + explain dry run.")]
    public static PromptMessage SafeWrite(
        [Description("Opaque connection id returned by db_connect.")] string connectionId,
        [Description("The destructive SQL statement to wrap.")] string sql)
        => UserMessage(
            $"Run the following SQL safely on connection '{connectionId}':\n\n{sql}\n\n"
            + "Before executing, call db_explain to show the plan, then call db_execute with confirm=false "
            + "so the user is asked to confirm via elicitation. Only proceed if the user accepts.");

    [McpServerPrompt(Name = "migrate-script")]
    [Description("Drafts a migration script for a target schema change.")]
    public static PromptMessage MigrateScript(
        [Description("Opaque connection id returned by db_connect.")] string connectionId,
        [Description("Plain-English description of the desired schema change.")] string goal)
        => UserMessage(
            $"On connection '{connectionId}', draft a forward + reverse migration for: {goal}. "
            + "First call db_table_describe on any touched tables, then produce idempotent SQL, "
            + "and finally suggest saving it via scripts_create.");

    [McpServerPrompt(Name = "explain-slow-query")]
    [Description("Explains a slow query using the provider's native plan format.")]
    public static PromptMessage ExplainSlow(
        [Description("Opaque connection id returned by db_connect.")] string connectionId,
        [Description("The SQL to explain.")] string sql)
        => UserMessage(
            $"Explain why the following query might be slow on connection '{connectionId}':\n\n{sql}\n\n"
            + "Call db_explain, then walk the user through the plan and suggest index or rewrite candidates.");

    private static PromptMessage UserMessage(string text) => new()
    {
        Role = Role.User,
        Content = new TextContentBlock { Text = text },
    };
}
