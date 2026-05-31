using System.Xml.Linq;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Discovers C# modules from .sln and .csproj files.
/// Parses project XML directly — no MSBuild dependency required.
/// </summary>
public sealed class RoslynModuleDiscovery : IModuleDiscovery
{
    private readonly IFileSystem _fs;
    private readonly List<SkippedFile> _lastSkipped = new();

    /// <summary>
    /// Diagnostic IDs that MSBuild's <c>Microsoft.CSharp.CurrentVersion.targets</c>
    /// adds to every csc invocation's <c>/nowarn</c> flag by default
    /// (the <c>&lt;NoWarn&gt;$(NoWarn);1701;1702&lt;/NoWarn&gt;</c>
    /// baseline). Lifeblood unions these into every discovered
    /// module's <see cref="ModuleInfo.NoWarnDiagnosticIds"/> so the
    /// compile-time diagnostic stream matches what the workspace's own
    /// <c>dotnet build</c> would produce. Both IDs are cross-module
    /// TypeRef binding-redirect warnings (CS1701: "assuming assembly
    /// reference matches identity"; CS1702: same family with a stricter
    /// version comparison shape). They are documented MSBuild defaults,
    /// not Lifeblood opinions. INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001.
    /// </summary>
    private static readonly string[] MsbuildImplicitNoWarnBaseline = new[] { "CS1701", "CS1702" };

    public RoslynModuleDiscovery(IFileSystem fs) => _fs = fs;

    /// <summary>
    /// Files the most recent <see cref="DiscoverModules"/> call dropped,
    /// with the reason code. Populated for .cs files listed in a csproj
    /// via explicit <c>&lt;Compile&gt;</c> items whose target path does
    /// not exist on disk. Reset at the start of every discovery run so
    /// callers always see a fresh picture.
    /// </summary>
    public IReadOnlyList<SkippedFile> LastDiscoverySkipped => _lastSkipped;

    public ModuleInfo[] DiscoverModules(string projectRoot)
    {
        _lastSkipped.Clear();

        // Try .sln first
        var slnFiles = _fs.FindFiles(projectRoot, "*.sln", recursive: false);
        if (slnFiles.Length > 0)
            return DiscoverFromSolution(slnFiles[0], projectRoot);

        // Fall back to .csproj files
        var csprojFiles = _fs.FindFiles(projectRoot, "*.csproj", recursive: true);
        return csprojFiles.Select(f => ParseProject(f, projectRoot)).Where(m => m != null).ToArray()!;
    }

    private ModuleInfo[] DiscoverFromSolution(string slnPath, string projectRoot)
    {
        var slnDir = Path.GetDirectoryName(slnPath)!;
        var modules = new List<ModuleInfo>();

        foreach (var line in _fs.ReadLines(slnPath))
        {
            // Match: Project("{...}") = "Name", "Path.csproj", "{...}"
            if (!line.StartsWith("Project(")) continue;

            var parts = line.Split('"');
            if (parts.Length < 6) continue;

            var relativePath = parts[5];
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            // .sln files conventionally use Windows backslashes in project paths,
            // but this code runs cross-platform. Normalize to the host's native
            // directory separator before combining, otherwise Path.Combine on
            // Linux/macOS treats "Lib\Lib.csproj" as a single filename containing
            // a literal backslash and FileExists always returns false.
            var fullPath = Path.GetFullPath(Path.Combine(slnDir, Internal.CsprojPaths.NormalizeSeparators(relativePath)));
            if (!_fs.FileExists(fullPath)) continue;

            var module = ParseProject(fullPath, projectRoot);
            if (module != null) modules.Add(module);
        }

        return modules.ToArray();
    }

    private ModuleInfo? ParseProject(string csprojPath, string projectRoot)
    {
        try
        {
            var xml = _fs.ReadAllText(csprojPath);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var projectDir = Path.GetDirectoryName(csprojPath)!;

            // Assembly name
            var assemblyName = doc.Descendants()
                .FirstOrDefault(el => el.Name.LocalName == "AssemblyName")?.Value
                ?? Path.GetFileNameWithoutExtension(csprojPath);

            // Source files — sorted for deterministic output (INV-PIPE-001)
            // Two strategies:
            //   SDK-style projects (modern .NET): no <Compile> items, scan filesystem.
            //   Old-format projects (Unity-generated): explicit <Compile Include="..."/> items.
            // Unity csproj files list every .cs file explicitly. Scanning the filesystem
            // would be catastrophically slow (75 projects × full recursive scan of project root).
            var compileItemCandidates = doc.Descendants()
                .Where(el => el.Name.LocalName == "Compile")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null && v.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(v => Path.GetFullPath(Path.Combine(projectDir, Internal.CsprojPaths.NormalizeSeparators(v!))))
                .ToArray();

            // Surface files that the csproj lists but the filesystem does
            // not contain. Without this, the analyzer silently filters them
            // out and users have no way to discover "why isn't my file in
            // the graph?".
            var compileItems = new List<string>(compileItemCandidates.Length);
            foreach (var path in compileItemCandidates)
            {
                if (_fs.FileExists(path))
                {
                    compileItems.Add(path);
                }
                else
                {
                    _lastSkipped.Add(new SkippedFile
                    {
                        FilePath = path,
                        Reason = SkipReason.FileNotFound,
                        ModuleName = assemblyName,
                    });
                }
            }

            string[] sourceFiles;
            if (compileItems.Count > 0 || compileItemCandidates.Length > 0)
            {
                // Old-format project with explicit Compile items (Unity, legacy .NET Framework).
                // Trust the csproj — do NOT scan the filesystem. Unity regenerates csprojs
                // frequently, and scanning 75 projects rooted under the same Assets/ tree
                // causes 75 recursive scans of the entire project (~minutes of hang).
                // NOTE: we check `compileItemCandidates.Length > 0` so that a csproj
                // which only lists MISSING files still takes the explicit-list branch
                // (rather than falling through to filesystem scan and ghost-including
                // files that weren't actually supposed to be compiled).
                sourceFiles = compileItems
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();
            }
            else
            {
                // SDK-style project — no Compile items, use filesystem scan
                var filesOnDisk = _fs.FindFiles(projectDir, "*.cs", recursive: true)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !f.Contains("/bin/") && !f.Contains("/obj/"))
                    .ToArray();
                sourceFiles = filesOnDisk
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();
            }

            // Project references → dependencies by AssemblyName (not filename)
            // SDK-style: <ProjectReference Include="..."/>
            // Unity-style: no ProjectReference, but <Reference Include="AssemblyName"/>
            var projectRefs = doc.Descendants()
                .Where(el => el.Name.LocalName == "ProjectReference")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => ResolveReferencedAssemblyName(v!, projectDir));

            // For Unity projects: <Reference Include="Some.AssemblyName">
            // These are assembly references to other project assemblies in the same solution.
            var assemblyRefs = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null && !v.StartsWith("System", StringComparison.Ordinal)
                          && !v.StartsWith("Microsoft", StringComparison.Ordinal)
                          && !v.StartsWith("Unity", StringComparison.Ordinal)
                          && !v.StartsWith("Mono", StringComparison.Ordinal)
                          && !v.StartsWith("mscorlib", StringComparison.Ordinal)
                          && !v.StartsWith("netstandard", StringComparison.Ordinal)
                          && !v.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                          && !v.StartsWith("Newtonsoft", StringComparison.Ordinal));

            var deps = projectRefs.Concat(assemblyRefs!.Select(v => v!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // External DLL references via HintPath.
            // These are non-module, non-NuGet assemblies (e.g., Unity engine DLLs)
            // that Roslyn needs as metadata references for accurate compilation.
            // HintPath values often contain Windows backslashes even in csprojs
            // generated on non-Windows tooling — normalize before combining.
            var externalDlls = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Select(el => el.Elements()
                    .FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value)
                .Where(v => v != null)
                .Select(v => Path.GetFullPath(Path.Combine(projectDir, Internal.CsprojPaths.NormalizeSeparators(v!))))
                .Where(_fs.FileExists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // BCL ownership detection (INV-BCL-002 / INV-BCL-004).
            // Walk every <Reference> element and ask whether it declares a base
            // class library. If ANY reference does, this module ships its own BCL
            // and the compilation builder must not inject the host BCL bundle.
            // See .claude/plans/bcl-ownership-fix.md §3 for the signal hierarchy
            // and §4 for why this lives in discovery (single source of truth).
            bool ownsBcl = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Any(ReferenceDeclaresBcl);

            // Compilation fact: <AllowUnsafeBlocks> (INV-COMPFACT-001..003).
            // Csproj-driven compilation options follow the same discover→store→
            // consume pattern as BCL ownership: parse here once, store on
            // ModuleInfo, consume in ModuleCompilationBuilder. NEVER re-derive
            // at the compilation layer.
            //
            // The MSBuild property name is "AllowUnsafeBlocks". Csproj allows
            // either case ("true" / "True"); Unity emits "True". The match is
            // case-insensitive on the value.
            bool allowUnsafeCode = doc.Descendants()
                .Where(el => el.Name.LocalName == "AllowUnsafeBlocks")
                .Select(el => el.Value)
                .Any(v => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

            // Compilation fact: <ImplicitUsings> (INV-COMPFACT-001..003).
            // When enabled, MSBuild generates global usings for System,
            // System.Collections.Generic, System.IO, System.Linq, etc.
            // Without these, Roslyn can't resolve List<>, Dictionary<>, etc.
            bool implicitUsings = doc.Descendants()
                .Where(el => el.Name.LocalName == "ImplicitUsings")
                .Select(el => el.Value)
                .Any(v => string.Equals(v?.Trim(), "enable", StringComparison.OrdinalIgnoreCase));

            // Compilation fact: <DefineConstants> (INV-COMPFACT-001..003 +
            // INV-DIAGNOSTIC-ENVELOPE-DEFINES-001). MSBuild concatenates every
            // <DefineConstants> in the csproj (and PropertyGroup conditions,
            // but Lifeblood doesn't evaluate MSBuild conditions — Unity emits
            // the fully-expanded form). The value is a semicolon-separated
            // list of preprocessor symbol names. Split, trim, drop empties.
            // Multiple <DefineConstants> elements are unioned in declaration
            // order; later identical entries deduplicate naturally because
            // CSharpParseOptions.WithPreprocessorSymbols treats the symbol
            // set as a set, not a multiset.
            var preprocessorSymbols = doc.Descendants()
                .Where(el => el.Name.LocalName == "DefineConstants")
                .SelectMany(el => (el.Value ?? string.Empty).Split(';'))
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Compilation fact: <LangVersion> (INV-COMPFACT-001..003 +
            // LB-FOLLOWUP-001). Take the LAST <LangVersion> element's value
            // — MSBuild semantics: later property assignments win. Stored
            // raw; Roslyn's LanguageVersionFacts.TryParse handles the
            // string-to-enum mapping at the compilation seam. Default
            // empty string means "csproj did not declare it" → builder
            // uses LanguageVersion.Default.
            var languageVersion = doc.Descendants()
                .Where(el => el.Name.LocalName == "LangVersion")
                .Select(el => el.Value?.Trim() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .LastOrDefault() ?? string.Empty;

            // Compilation fact: <TargetFramework> / <TargetFrameworks>. MSBuild
            // framework references contribute source-generator analyzers from
            // the matching Microsoft.NETCore.App.Ref pack (for example
            // System.Text.Json.SourceGeneration.dll). Lifeblood compiles
            // source directly, so discovery surfaces the target framework and
            // framework source-generator paths as module facts for the
            // compilation seam to run. Multi-target projects are represented by
            // one ModuleInfo today; use the first TFM as the same conservative
            // single-compilation posture the rest of discovery already takes.
            var targetFramework = ReadTargetFramework(doc);
            var sourceGeneratorAnalyzerPaths = DiscoverFrameworkSourceGeneratorAnalyzerPaths(targetFramework);

            // Compilation fact: <Nullable> (INV-COMPFACT-001..003 +
            // LB-FOLLOWUP-002). Take the LAST <Nullable> element's value
            // (MSBuild "later wins"). Stored raw — values "enable" /
            // "disable" / "warnings" / "annotations" map at the
            // compilation seam to NullableContextOptions. Empty string
            // means "csproj did not declare it" → builder uses Disable.
            var nullableContext = doc.Descendants()
                .Where(el => el.Name.LocalName == "Nullable")
                .Select(el => el.Value?.Trim() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .LastOrDefault() ?? string.Empty;

            // Compilation fact: <NoWarn> (INV-COMPFACT-001..003 +
            // LB-FOLLOWUP-003). Multiple <NoWarn> elements union;
            // semicolon split, trim, drop empties, dedup. Threaded at
            // compilation time into CSharpCompilationOptions.WithSpecificDiagnosticOptions
            // mapping each ID to ReportDiagnostic.Suppress.
            //
            // INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001: union the
            // csproj-declared set with MSBuild's csc-default suppression
            // baseline. Microsoft.CSharp.CurrentVersion.targets sets
            //   <NoWarn>$(NoWarn);1701;1702</NoWarn>
            // for every csc invocation, so any `dotnet build` against an
            // SDK-style or framework-style csproj already silences both
            // IDs — they are cross-module TypeRef binding-redirect
            // warnings that fire per consuming reference whenever an
            // upstream PE's recorded version of a transitively-shared
            // assembly disagrees with the version currently loaded
            // (e.g. xunit.core baked against System.Runtime 4.0.0.0 vs
            // BCL ref pack 8.0.0.0). Lifeblood mirrors what the
            // workspace's own toolchain sees; not mirroring means
            // diagnose ships 7,000+ warnings the consumer's CI does not.
            // User-declared <NoWarn> still wins over the baseline (union
            // semantics), and any module that genuinely wants 1701/1702
            // back can use <WarningsNotAsErrors> + per-module override —
            // exactly the same escape hatch MSBuild offers.
            var declaredNoWarn = doc.Descendants()
                .Where(el => el.Name.LocalName == "NoWarn")
                .SelectMany(el => (el.Value ?? string.Empty).Split(';', ','))
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            var noWarnIds = declaredNoWarn
                .Concat(MsbuildImplicitNoWarnBaseline)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Compilation fact: <Features> (INV-RUNTIME-ASYNC-COMPAT-001).
            // Roslyn exposes CSharpParseOptions.WithFeatures for experimental
            // compiler switches. Runtime Async projects can opt in via
            // <Features>runtime-async=on</Features>; Lifeblood must preserve
            // that project fact when it parses source and when compile_check
            // replaces syntax trees. Take the LAST <Features> element's value
            // (MSBuild property assignment semantics) and parse its semicolon-
            // separated name/value pairs without interpreting the names.
            var compilerFeatures = ParseCompilerFeatures(
                doc.Descendants()
                    .Where(el => el.Name.LocalName == "Features")
                    .Select(el => el.Value?.Trim() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .LastOrDefault() ?? string.Empty);

            // Compilation fact: <InternalsVisibleTo Include="X" /> items
            // (INV-DIAGNOSTIC-IVT-PARITY-001 / INV-COMPFACT-001..003).
            // MSBuild's GenerateAssemblyInfo target turns each item into an
            // [assembly: InternalsVisibleTo("X")] attribute emitted onto the
            // producer PE via obj/<Tfm>/<AssemblyName>.AssemblyInfo.cs. The
            // SDK-style source scan above (line ~139) skips obj/, so the
            // generated file never reaches the compilation; without surfacing
            // the items the emitted PE has no IVT metadata and every internal
            // access from a friend module fails with CS0122. Surface the items
            // here so the compilation seam can synthesize an equivalent
            // attribute tree — same outcome MSBuild would produce on disk.
            // Multiple item-group entries union; trim + dedupe by ordinal.
            var internalsVisibleTo = doc.Descendants()
                .Where(el => el.Name.LocalName == "InternalsVisibleTo")
                .Select(el => el.Attribute("Include")?.Value?.Trim() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Pure detection: no PackageReference or assembly Reference
            bool isPure = !doc.Descendants().Any(el =>
                el.Name.LocalName == "PackageReference"
                || el.Name.LocalName == "Reference");

            // Reference closure detection (INV-MODULE-REFS-001).
            // Old-format MSBuild 2003-schema csprojs (Unity asmdef generators,
            // legacy .NET Framework projects) signature: xmlns of the root
            // Project element is "http://schemas.microsoft.com/developer/msbuild/2003"
            // AND there is no Sdk attribute. That schema's MSBuild targets
            // never close ProjectReference transitively, so the source-of-truth
            // compile classpath is direct-deps-only. SDK-style csprojs
            // (<Project Sdk="..."> with no schema namespace) DO close
            // transitively at build time, so Lifeblood mirrors that.
            // Detection is csproj-shape only — no path heuristics, no Unity-
            // specific markers, no filename sniffing. INV-MODULE-REFS-001.
            bool hasSdkAttribute = !string.IsNullOrEmpty(doc.Root?.Attribute("Sdk")?.Value);
            bool isOldFormatSchema = ns.NamespaceName == "http://schemas.microsoft.com/developer/msbuild/2003";
            var referenceClosure = (isOldFormatSchema && !hasSdkAttribute)
                ? ReferenceClosureMode.DirectOnly
                : ReferenceClosureMode.Transitive;

            return new ModuleInfo
            {
                Name = assemblyName,
                FilePaths = sourceFiles,
                Dependencies = deps,
                IsPure = isPure,
                ExternalDllPaths = externalDlls,
                BclOwnership = ownsBcl
                    ? BclOwnershipMode.ModuleProvided
                    : BclOwnershipMode.HostProvided,
                AllowUnsafeCode = allowUnsafeCode,
                ImplicitUsings = implicitUsings,
                PreprocessorSymbols = preprocessorSymbols,
                LanguageVersion = languageVersion,
                TargetFramework = targetFramework,
                NullableContext = nullableContext,
                NoWarnDiagnosticIds = noWarnIds,
                CompilerFeatures = compilerFeatures,
                ReferenceClosure = referenceClosure,
                InternalsVisibleTo = internalsVisibleTo,
                SourceGeneratorAnalyzerPaths = sourceGeneratorAnalyzerPaths,
                Properties = new Dictionary<string, string>
                {
                    ["projectFile"] = Path.GetRelativePath(projectRoot, csprojPath).Replace('\\', '/'),
                },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // BCL ownership detection helpers (INV-BCL-002 / INV-BCL-004)
    //
    // A module is "BCL-owning" iff its csproj declares a base class library
    // via either:
    //   (1) a <Reference Include="netstandard|mscorlib|System.Runtime"> element,
    //       parsed as an assembly identity (handles both the bare form used by
    //       modern Unity csprojs and the strong-name form used by legacy
    //       .NET Framework / NuGet-converted csprojs), OR
    //   (2) a <Reference> with a <HintPath> child whose file basename
    //       (sans .dll extension, case-insensitive) is one of those names.
    //
    // The Include-attribute signal is primary because it's the most-authoritative
    // declaration the csproj makes. The HintPath signal is the backstop for
    // csprojs that omit Include or use a non-canonical Include name.
    //
    // Detection logic lives ONLY here. ModuleCompilationBuilder consumes the
    // resulting BclOwnership field and never re-derives it from filenames.
    // ────────────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> ParseCompilerFeatures(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var features = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in raw.Split(';'))
        {
            var part = token.Trim();
            if (part.Length == 0) continue;

            var equals = part.IndexOf('=');
            var name = equals < 0 ? part : part.Substring(0, equals).Trim();
            if (name.Length == 0) continue;

            var value = equals < 0 ? string.Empty : part.Substring(equals + 1).Trim();
            features[name] = value;
        }

        return features;
    }

    private static string ReadTargetFramework(XDocument doc)
    {
        var single = doc.Descendants()
            .Where(el => el.Name.LocalName == "TargetFramework")
            .Select(el => el.Value?.Trim() ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .LastOrDefault();
        if (!string.IsNullOrEmpty(single))
            return single!;

        var multi = doc.Descendants()
            .Where(el => el.Name.LocalName == "TargetFrameworks")
            .Select(el => el.Value?.Trim() ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .LastOrDefault();
        if (string.IsNullOrEmpty(multi))
            return string.Empty;

        return multi!
            .Split(';')
            .Select(s => s.Trim())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s))
            ?? string.Empty;
    }

    private static string[] DiscoverFrameworkSourceGeneratorAnalyzerPaths(string targetFramework)
    {
        var major = ParseNetTargetFrameworkMajor(targetFramework);
        if (major == null)
            return Array.Empty<string>();

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var dotnetRoot = runtimeDir == null
            ? null
            : Directory.GetParent(runtimeDir)?.Parent?.Parent?.FullName;
        if (string.IsNullOrEmpty(dotnetRoot))
            return Array.Empty<string>();

        var packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packRoot))
            return Array.Empty<string>();

        var packDir = new DirectoryInfo(packRoot)
            .EnumerateDirectories()
            .Select(d => (Directory: d, Version: TryParseVersion(d.Name, out var version) ? version : null))
            .Where(candidate => candidate.Version != null && candidate.Version.Major == major.Value)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Directory)
            .FirstOrDefault();
        if (packDir == null)
            return Array.Empty<string>();

        var analyzerDir = Path.Combine(packDir.FullName, "analyzers", "dotnet", "cs");
        if (!Directory.Exists(analyzerDir))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(analyzerDir, "*.dll")
            .Where(path => Path.GetFileName(path).EndsWith("Generator.dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Contains("SourceGeneration", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static int? ParseNetTargetFrameworkMajor(string targetFramework)
    {
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return null;

        var start = 3;
        var end = start;
        while (end < targetFramework.Length && char.IsDigit(targetFramework[end]))
            end++;

        return end == start
            ? null
            : int.Parse(targetFramework.Substring(start, end - start));
    }

    internal static bool TryParseVersion(string raw, out Version version)
    {
        var prereleaseStart = raw.IndexOf('-');
        var stablePart = prereleaseStart < 0
            ? raw
            : raw.Substring(0, prereleaseStart);

        return Version.TryParse(stablePart, out version!);
    }

    private static bool ReferenceDeclaresBcl(XElement referenceElement)
    {
        var include = referenceElement.Attribute("Include")?.Value ?? "";
        var simpleName = ParseAssemblyIdentitySimpleName(include);
        if (IsBclSimpleName(simpleName)) return true;

        var hintPath = referenceElement.Elements()
            .FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value;
        if (string.IsNullOrEmpty(hintPath)) return false;

        var hintBasename = Path.GetFileNameWithoutExtension(hintPath);
        return IsBclSimpleName(hintBasename);
    }

    /// <summary>
    /// Extract the simple assembly name from an Include attribute value.
    /// Handles both the bare form (<c>"netstandard"</c>) and the strong-name
    /// form (<c>"netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=..."</c>).
    /// </summary>
    internal static string ParseAssemblyIdentitySimpleName(string includeValue)
    {
        if (string.IsNullOrWhiteSpace(includeValue)) return "";
        var commaIdx = includeValue.IndexOf(',');
        var firstToken = commaIdx < 0 ? includeValue : includeValue.Substring(0, commaIdx);
        return firstToken.Trim();
    }

    /// <summary>
    /// Returns true iff the assembly's simple name is one of the three base
    /// class libraries Lifeblood recognizes for ownership detection.
    /// <c>System.Private.CoreLib</c> is intentionally excluded — it's the
    /// .NET 8 implementation assembly, not a reference assembly that any
    /// csproj declares.
    /// </summary>
    internal static bool IsBclSimpleName(string name) =>
        name.Equals("netstandard",    StringComparison.OrdinalIgnoreCase)
     || name.Equals("mscorlib",       StringComparison.OrdinalIgnoreCase)
     || name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a ProjectReference path to the referenced project's AssemblyName.
    /// Falls back to the bare module name if the referenced .csproj can't be read.
    /// All path-shaped csproj attribute parsing routes through
    /// <see cref="Internal.CsprojPaths"/> so production discovery and the
    /// architecture ratchet test share one source of truth and can never drift.
    /// </summary>
    private string ResolveReferencedAssemblyName(string referencePath, string projectDir)
    {
        var normalized = Internal.CsprojPaths.NormalizeSeparators(referencePath);
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDir, normalized));
            if (!_fs.FileExists(fullPath))
                return Internal.CsprojPaths.GetReferencedModuleName(referencePath);

            var xml = _fs.ReadAllText(fullPath);
            var refDoc = XDocument.Parse(xml);
            var asmName = refDoc.Descendants()
                .FirstOrDefault(el => el.Name.LocalName == "AssemblyName")?.Value;

            return asmName ?? Internal.CsprojPaths.GetReferencedModuleName(referencePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Internal.CsprojPaths.GetReferencedModuleName(referencePath);
        }
    }
}
