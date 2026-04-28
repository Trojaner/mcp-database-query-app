using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Options governing how the OAuth2 resolver provisions and selects profiles.
/// </summary>
public sealed class ProfileResolutionOptions
{
    /// <summary>
    /// When true, the resolver will create a new profile on first sight of an
    /// unknown <c>(issuer, subject)</c> pair. When false, unknown identities
    /// surface as a <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public bool AutoProvisionProfiles { get; set; } = true;
}

/// <summary>
/// Resolves the request to a profile keyed by the validated bearer token's
/// <c>(issuer, subject)</c> claim pair. Falls through (returns <c>null</c>)
/// when no auth context is present so a downstream resolver can run.
/// </summary>
public sealed class OAuth2ProfileResolver : IProfileResolver
{
    private readonly IProfileStore _profiles;
    private readonly ProfileResolutionOptions _options;
    private readonly ILogger<OAuth2ProfileResolver> _logger;

    /// <summary>
    /// Creates a new <see cref="OAuth2ProfileResolver"/>.
    /// </summary>
    public OAuth2ProfileResolver(
        IProfileStore profiles,
        ProfileResolutionOptions options,
        ILogger<OAuth2ProfileResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _profiles = profiles;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Profile?> ResolveAsync(IProfileAuthContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsAuthenticated || string.IsNullOrEmpty(context.Subject))
        {
            return null;
        }

        var existing = await _profiles
            .FindByIdentityAsync(context.Issuer, context.Subject, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _logger.LogDebug(
                "Resolved profile {ProfileId} for (issuer={Issuer}, subject={Subject})",
                existing.Id.Value,
                context.Issuer ?? "(none)",
                context.Subject);
            return existing;
        }

        if (!_options.AutoProvisionProfiles)
        {
            _logger.LogWarning(
                "Rejected unknown OAuth2 identity (issuer={Issuer}, subject={Subject}); auto-provisioning disabled",
                context.Issuer ?? "(none)",
                context.Subject);
            throw new UnauthorizedAccessException(
                "No profile is provisioned for the presented OAuth2 identity, and auto-provisioning is disabled.");
        }

        var newId = new ProfileId(DeriveProfileId(context.Issuer, context.Subject));
        var name = string.IsNullOrWhiteSpace(context.PreferredName) ? context.Subject : context.PreferredName;
        var profile = new Profile(
            newId,
            name!,
            context.Subject,
            context.Issuer,
            DateTimeOffset.UtcNow,
            ProfileStatus.Active,
            new Dictionary<string, string>(StringComparer.Ordinal));

        var stored = await _profiles.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Auto-provisioned profile {ProfileId} for (issuer={Issuer}, subject={Subject})",
            stored.Id.Value,
            context.Issuer ?? "(none)",
            context.Subject);
        return stored;
    }

    /// <summary>
    /// Produces a deterministic, opaque profile id from the OAuth2 identity
    /// pair. The id is short, URL-safe and never reveals the underlying
    /// claims. Callers can treat it as an opaque sandbox key.
    /// </summary>
    public static string DeriveProfileId(string? issuer, string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var seed = $"{issuer ?? string.Empty}|{subject}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // 12 bytes = 24 hex chars: ample collision margin for a per-server profile namespace.
        return "p_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}

/// <summary>
/// Composite resolver that chains the OAuth2 resolver in front of a default
/// resolver. Returns the first non-null result.
/// </summary>
public sealed class CompositeProfileResolver : IProfileResolver
{
    private readonly IReadOnlyList<IProfileResolver> _chain;

    /// <summary>
    /// Creates a new composite from an ordered set of resolvers. The
    /// <paramref name="chain"/> is evaluated in order; the first resolver
    /// that returns a non-null profile wins.
    /// </summary>
    public CompositeProfileResolver(IEnumerable<IProfileResolver> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        _chain = chain.ToList();
        if (_chain.Count == 0)
        {
            throw new ArgumentException("At least one resolver is required.", nameof(chain));
        }
    }

    /// <inheritdoc/>
    public async Task<Profile?> ResolveAsync(IProfileAuthContext context, CancellationToken cancellationToken)
    {
        foreach (var resolver in _chain)
        {
            var profile = await resolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
            if (profile is not null)
            {
                return profile;
            }
        }

        throw new InvalidOperationException("No profile resolver in the chain produced a profile.");
    }
}
