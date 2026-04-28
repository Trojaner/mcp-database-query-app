namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Bitmask of operations an <see cref="AclEntry"/> may permit or deny against
/// a target object. Each operation aligns 1:1 with a
/// <see cref="QueryParsing.ActionKind"/> value so the pipeline step can map
/// directly without lookup tables.
/// </summary>
/// <remarks>
/// <para>
/// Bit layout (do NOT renumber once persisted; the integer is stored verbatim):
/// </para>
/// <list type="bullet">
///   <item><description><c>0x0001</c> — <see cref="Read"/> (SELECT, projected/filtered columns).</description></item>
///   <item><description><c>0x0002</c> — <see cref="Insert"/> (INSERT).</description></item>
///   <item><description><c>0x0004</c> — <see cref="Update"/> (UPDATE).</description></item>
///   <item><description><c>0x0008</c> — <see cref="Delete"/> (DELETE).</description></item>
///   <item><description><c>0x0010</c> — <see cref="Truncate"/> (TRUNCATE TABLE).</description></item>
///   <item><description><c>0x0020</c> — <see cref="Create"/> (CREATE …).</description></item>
///   <item><description><c>0x0040</c> — <see cref="Alter"/> (ALTER …).</description></item>
///   <item><description><c>0x0080</c> — <see cref="Drop"/> (DROP …).</description></item>
///   <item><description><c>0x0100</c> — <see cref="Execute"/> (EXEC procedure / function).</description></item>
///   <item><description><c>0x0200</c> — <see cref="Grant"/> (GRANT statements).</description></item>
///   <item><description><c>0x0400</c> — <see cref="Revoke"/> (REVOKE statements).</description></item>
/// </list>
/// </remarks>
[Flags]
public enum AclOperation
{
    /// <summary>No operations.</summary>
    None = 0,

    /// <summary>SELECT and any read-only projection of the target.</summary>
    Read = 1 << 0,

    /// <summary>INSERT against the target.</summary>
    Insert = 1 << 1,

    /// <summary>UPDATE against the target.</summary>
    Update = 1 << 2,

    /// <summary>DELETE against the target.</summary>
    Delete = 1 << 3,

    /// <summary>TRUNCATE TABLE.</summary>
    Truncate = 1 << 4,

    /// <summary>CREATE … DDL.</summary>
    Create = 1 << 5,

    /// <summary>ALTER … DDL.</summary>
    Alter = 1 << 6,

    /// <summary>DROP … DDL.</summary>
    Drop = 1 << 7,

    /// <summary>EXEC procedure or callable function.</summary>
    Execute = 1 << 8,

    /// <summary>GRANT statements.</summary>
    Grant = 1 << 9,

    /// <summary>REVOKE statements.</summary>
    Revoke = 1 << 10,

    /// <summary>
    /// Convenience union of every concrete operation. Use sparingly — entries
    /// with <see cref="All"/> generally indicate an over-broad rule.
    /// </summary>
    All = Read | Insert | Update | Delete | Truncate | Create | Alter | Drop | Execute | Grant | Revoke,
}
