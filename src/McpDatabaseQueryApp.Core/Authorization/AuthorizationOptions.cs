namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Bound to the <c>McpDatabaseQueryApp:Authorization</c> configuration
/// section. Controls the ACL's default-profile policy, the static ACL seed
/// loaded at startup, and the in-memory cache TTL the evaluator applies to
/// stored entries.
/// </summary>
public sealed class AuthorizationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "McpDatabaseQueryApp:Authorization";

    /// <summary>
    /// Behaviour for the built-in <see cref="Profiles.ProfileId.Default"/>
    /// profile when no entries match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DefaultProfilePolicy.AllowAll"/> short-circuits the
    /// evaluator with an unconditional <see cref="AclEffect.Allow"/> for the
    /// default profile, regardless of stored entries. This preserves the
    /// pre-ACL behaviour for installations that have not yet configured per-
    /// object rules. <strong>Security note:</strong> AllowAll on the default
    /// profile means any client that does not present an OAuth2 identity has
    /// unrestricted access. Treat AllowAll as a backwards-compatibility seam
    /// only; long-lived deployments should switch to
    /// <see cref="DefaultProfilePolicy.DenyAll"/> and seed explicit Allow
    /// rules.
    /// </para>
    /// <para>
    /// <see cref="DefaultProfilePolicy.DenyAll"/> evaluates the default
    /// profile against the stored ACL just like every other profile: an
    /// empty rule set means default-deny.
    /// </para>
    /// </remarks>
    public DefaultProfilePolicy DefaultProfilePolicy { get; set; } = DefaultProfilePolicy.AllowAll;

    /// <summary>
    /// Static ACL seed loaded into the evaluator at startup. Static entries
    /// are merged on top of stored entries with a +1000 priority offset so
    /// they always outrank operator-managed rules. They are NOT persisted to
    /// SQLite and the REST API cannot mutate them — operators change them
    /// only by editing <c>appsettings.json</c> and restarting.
    /// </summary>
    public List<StaticAclEntryConfig> StaticEntries { get; set; } = new();

    /// <summary>
    /// In-memory cache lifetime for the per-profile entry list. Defaults to
    /// 30 seconds. Set to <see cref="TimeSpan.Zero"/> to disable caching
    /// (every evaluation reads from the store).
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Default-profile bootstrapping behaviour. See
/// <see cref="AuthorizationOptions.DefaultProfilePolicy"/>.
/// </summary>
public enum DefaultProfilePolicy
{
    /// <summary>
    /// Bypass ACL evaluation for the default profile and unconditionally
    /// allow every operation. Backwards-compatible default.
    /// </summary>
    AllowAll = 0,

    /// <summary>
    /// Apply normal ACL evaluation to the default profile. Empty rule set
    /// means default-deny.
    /// </summary>
    DenyAll = 1,
}

/// <summary>
/// Configuration shape for one entry of
/// <see cref="AuthorizationOptions.StaticEntries"/>. Mirrors
/// <see cref="AclEntry"/> but uses primitive/string types friendly to
/// JSON config binding.
/// </summary>
public sealed class StaticAclEntryConfig
{
    /// <summary>Required. Profile this entry applies to.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Subject discriminator. Defaults to <see cref="AclSubjectKind.Profile"/>.</summary>
    public AclSubjectKind SubjectKind { get; set; } = AclSubjectKind.Profile;

    /// <summary>Allow or deny.</summary>
    public AclEffect Effect { get; set; } = AclEffect.Allow;

    /// <summary>
    /// Operations the entry covers. Bind via the named flag values
    /// (e.g. <c>["Read","Insert"]</c>). Defaults to <see cref="AclOperation.None"/>.
    /// </summary>
    public List<AclOperation> AllowedOperations { get; set; } = new();

    /// <summary>Optional priority. Default 0; +1000 offset is added at load.</summary>
    public int Priority { get; set; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Scope: connection host (null wildcard).</summary>
    public string? Host { get; set; }

    /// <summary>Scope: connection port (null wildcard).</summary>
    public int? Port { get; set; }

    /// <summary>Scope: target database/catalog (null wildcard).</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Scope: target schema (null wildcard).</summary>
    public string? Schema { get; set; }

    /// <summary>Scope: target table (null wildcard).</summary>
    public string? Table { get; set; }

    /// <summary>Scope: target column (null wildcard).</summary>
    public string? Column { get; set; }
}
