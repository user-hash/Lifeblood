using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lifeblood.Domain.Workspaces;

/// <summary>
/// A domain-separated SHA-256 fingerprint. All analysis identity hashing uses
/// this value object so wire format, canonical framing, and equality cannot
/// drift between the host and language adapters.
/// </summary>
public sealed record ContentFingerprint
{
    private const string Prefix = "sha256_";

    private ContentFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ContentFingerprint ComputeUtf8(string domain, string content)
        => Compute(domain, new[] { content });

    public static ContentFingerprint Compute(string domain, IEnumerable<string> parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(parts);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var part in parts)
            Append(hash, part ?? throw new ArgumentException("Fingerprint parts cannot be null.", nameof(parts)));

        return FromHashBytes(hash.GetHashAndReset());
    }

    public static ContentFingerprint FromHashBytes(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 32)
            throw new ArgumentException("A SHA-256 hash must contain exactly 32 bytes.", nameof(hash));

        return new ContentFingerprint(Prefix + Convert.ToHexString(hash).ToLowerInvariant());
    }

    public static bool TryParse(string? value, out ContentFingerprint fingerprint)
    {
        fingerprint = null!;
        if (value == null
            || value.Length != Prefix.Length + 64
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = value.AsSpan(Prefix.Length);
        if (hex.IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            return false;
        }

        fingerprint = FromHashBytes(Convert.FromHexString(hex));
        return true;
    }

    public override string ToString() => Value;

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

/// <summary>
/// Opaque identity of one canonical physical workspace. Path discovery and
/// case normalization stay at the composition boundary; Domain owns the
/// stable value and comparison contract.
/// </summary>
public sealed record WorkspaceKey
{
    private const string Prefix = "workspace_";

    private WorkspaceKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static WorkspaceKey FromCanonicalIdentity(string canonicalIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalIdentity);
        var digest = ContentFingerprint.ComputeUtf8("lifeblood.workspace-key.v1", canonicalIdentity);
        return new WorkspaceKey(Prefix + digest.Value["sha256_".Length..]);
    }

    public override string ToString() => Value;
}

public enum RuleSetSourceKind
{
    None,
    BuiltIn,
    File,
}

/// <summary>
/// Semantic identity plus provenance for the effective architecture rule set.
/// Equality follows canonical rule content, not the incidental path or pack
/// name used to obtain that content.
/// </summary>
public sealed class RuleSetIdentity : IEquatable<RuleSetIdentity>
{
    public RuleSetIdentity(
        RuleSetSourceKind sourceKind,
        string? source,
        ContentFingerprint contentFingerprint)
    {
        SourceKind = sourceKind;
        Source = source;
        ContentFingerprint = contentFingerprint
            ?? throw new ArgumentNullException(nameof(contentFingerprint));
        Fingerprint = ContentFingerprint.Compute(
            "lifeblood.rule-set.v1",
            new[] { contentFingerprint.Value });
    }

    public RuleSetSourceKind SourceKind { get; }

    public string? Source { get; }

    public ContentFingerprint ContentFingerprint { get; }

    public ContentFingerprint Fingerprint { get; }

    public bool Equals(RuleSetIdentity? other)
        => other != null && Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as RuleSetIdentity);

    public override int GetHashCode() => Fingerprint.GetHashCode();
}

public enum AnalysisRetentionMode
{
    GraphOnly,
    RetainedSemantic,
}

public enum AnalysisDescriptorPolicy
{
    WorkspaceDiscovery,
    ImportedGraph,
}

/// <summary>
/// Canonical graph-shaping request. Profile order is semantic because the
/// first profile owns retained compilations; exclude-glob order is not and is
/// therefore normalized. Equality is the canonical fingerprint.
/// </summary>
public sealed class AnalysisSpec : IEquatable<AnalysisSpec>
{
    public AnalysisSpec(
        IEnumerable<string>? defineProfiles,
        IEnumerable<string>? excludePathGlobs,
        AnalysisRetentionMode retentionMode,
        AnalysisDescriptorPolicy descriptorPolicy,
        RuleSetIdentity ruleSet)
    {
        var normalizedProfiles = NormalizeProfiles(defineProfiles);
        var normalizedGlobs = NormalizeGlobs(excludePathGlobs);
        DefineProfiles = Array.AsReadOnly(normalizedProfiles);
        ExcludePathGlobs = Array.AsReadOnly(normalizedGlobs);
        RetentionMode = retentionMode;
        DescriptorPolicy = descriptorPolicy;
        RuleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));

        Fingerprint = ContentFingerprint.Compute(
            "lifeblood.analysis-spec.v1",
            new[]
            {
                retentionMode.ToString(),
                descriptorPolicy.ToString(),
                ruleSet.Fingerprint.Value,
                DefineProfiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }
            .Concat(DefineProfiles)
            .Concat(new[] { ExcludePathGlobs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .Concat(ExcludePathGlobs.Select(value => value.ToUpperInvariant())));
    }

    public IReadOnlyList<string> DefineProfiles { get; }

    public IReadOnlyList<string> ExcludePathGlobs { get; }

    public AnalysisRetentionMode RetentionMode { get; }

    public AnalysisDescriptorPolicy DescriptorPolicy { get; }

    public RuleSetIdentity RuleSet { get; }

    public ContentFingerprint Fingerprint { get; }

    public bool Equals(AnalysisSpec? other)
        => other != null && Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as AnalysisSpec);

    public override int GetHashCode() => Fingerprint.GetHashCode();

    private static string[] NormalizeProfiles(IEnumerable<string>? profiles)
    {
        if (profiles == null)
            return Array.Empty<string>();

        var normalized = profiles
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Define-profile names must be unique.", nameof(profiles));

        return normalized;
    }

    private static string[] NormalizeGlobs(IEnumerable<string>? globs)
        => globs?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray()
           ?? Array.Empty<string>();
}

public sealed record FingerprintEntry
{
    public FingerprintEntry(string path, ContentFingerprint content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path.Replace('\\', '/');
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public string Path { get; }

    public ContentFingerprint Content { get; }
}

/// <summary>
/// Authoritative content receipt for source and descriptor inputs consumed by
/// one analysis. Git state and mtimes may accelerate capture, but never define
/// equality.
/// </summary>
public sealed class SourceFingerprint : IEquatable<SourceFingerprint>
{
    public SourceFingerprint(
        IEnumerable<FingerprintEntry> sourceFiles,
        IEnumerable<FingerprintEntry> descriptorFiles)
    {
        var sources = Normalize(sourceFiles);
        var descriptors = Normalize(descriptorFiles);

        SourceFileCount = sources.Length;
        DescriptorFileCount = descriptors.Length;
        SourceContent = BuildSection("lifeblood.source-inputs.v1", sources);
        Descriptors = BuildSection("lifeblood.descriptor-inputs.v1", descriptors);
        Fingerprint = ContentFingerprint.Compute(
            "lifeblood.source-fingerprint.v1",
            new[] { SourceContent.Value, Descriptors.Value });
    }

    public int SourceFileCount { get; }

    public int DescriptorFileCount { get; }

    public ContentFingerprint SourceContent { get; }

    public ContentFingerprint Descriptors { get; }

    public ContentFingerprint Fingerprint { get; }

    public bool Equals(SourceFingerprint? other)
        => other != null && Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as SourceFingerprint);

    public override int GetHashCode() => Fingerprint.GetHashCode();

    private static FingerprintEntry[] Normalize(IEnumerable<FingerprintEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Fingerprint entry paths must be unique.", nameof(entries));

        return normalized;
    }

    private static ContentFingerprint BuildSection(string domain, IReadOnlyList<FingerprintEntry> entries)
        => ContentFingerprint.Compute(
            domain,
            new[] { entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(entries.SelectMany(entry => new[] { entry.Path, entry.Content.Value })));
}

public sealed record BaseKey
{
    private const string Prefix = "base_";

    public BaseKey(WorkspaceKey workspace, AnalysisSpec spec)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        var digest = ContentFingerprint.Compute(
            "lifeblood.base-key.v1",
            new[] { workspace.Value, spec.Fingerprint.Value });
        Value = Prefix + digest.Value["sha256_".Length..];
    }

    public WorkspaceKey Workspace { get; }

    public AnalysisSpec Spec { get; }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record AnalysisKey
{
    private const string Prefix = "analysis_";

    public AnalysisKey(BaseKey baseKey, SourceFingerprint source)
    {
        BaseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        var digest = ContentFingerprint.Compute(
            "lifeblood.analysis-key.v1",
            new[] { baseKey.Value, source.Fingerprint.Value });
        Value = Prefix + digest.Value["sha256_".Length..];
    }

    public BaseKey BaseKey { get; }

    public SourceFingerprint Source { get; }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// The single immutable authority for equality and provenance of one analyzed
/// workspace publication. Wire DTOs project this object; they never rebuild
/// any key from parallel fields.
/// </summary>
public sealed record WorkspaceAnalysisIdentity
{
    public WorkspaceAnalysisIdentity(
        WorkspaceKey workspace,
        AnalysisSpec spec,
        SourceFingerprint source)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        BaseKey = new BaseKey(workspace, spec);
        AnalysisKey = new AnalysisKey(BaseKey, source);
    }

    public WorkspaceKey Workspace { get; }

    public AnalysisSpec Spec { get; }

    public SourceFingerprint Source { get; }

    public BaseKey BaseKey { get; }

    public AnalysisKey AnalysisKey { get; }
}
