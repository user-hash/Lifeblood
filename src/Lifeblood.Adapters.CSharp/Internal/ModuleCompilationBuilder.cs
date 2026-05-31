using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Builds CSharpCompilations for modules in dependency order.
/// Supports two modes:
///   Streaming (default): compile → extract → downgrade → next module. O(1) compilation memory.
///   Retained: compile → extract → keep in memory for write-side tools. O(N) compilation memory.
///
/// Compilation downgrading: after extraction, Emit() to a byte[] and create a lightweight
/// MetadataReference.CreateFromImage(). Downstream modules reference the PE image (~10-100KB)
/// instead of the full compilation (~200MB). The full compilation becomes eligible for GC.
/// </summary>
internal sealed class ModuleCompilationBuilder
{
    private readonly IFileSystem _fs;
    private readonly NuGetReferenceResolver _nuget;
    private readonly SharedMetadataReferenceCache _refCache;

    private const string ImplicitGlobalUsingsSource = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    public ModuleCompilationBuilder(IFileSystem fs, SharedMetadataReferenceCache? refCache = null)
    {
        _fs = fs;
        _nuget = new NuGetReferenceResolver(fs);
        _refCache = refCache ?? new SharedMetadataReferenceCache();
    }

    /// <summary>
    /// Callback invoked for each module after its compilation is built.
    /// The compilation is valid only during the callback — after return,
    /// it may be downgraded to a lightweight metadata reference.
    /// </summary>
    internal delegate void CompilationProcessor(
        ModuleInfo module, CSharpCompilation compilation);

    /// <summary>
    /// Process modules in dependency order, one at a time.
    /// For each module: build compilation → invoke processor → downgrade or retain.
    /// </summary>
    /// <param name="modules">Discovered modules from IModuleDiscovery.</param>
    /// <param name="projectRoot">Workspace root path.</param>
    /// <param name="config">Analysis configuration (exclude patterns, retention flag).</param>
    /// <param name="processor">Called with each compilation for symbol/edge extraction.</param>
    /// <returns>
    /// Retained compilations if config.RetainCompilations is true; null otherwise.
    /// When null, compilations have been downgraded — only the graph survives.
    /// </returns>
    /// <summary>
    /// Process modules in dependency order, one at a time. See class summary
    /// for streaming-vs-retained memory tradeoff.
    /// </summary>
    /// <param name="modules">Modules to (re)compile. In a full analyze this is
    /// every discovered module; in an incremental analyze this is only the
    /// subset whose source files changed.</param>
    /// <param name="carryDowngraded">Per-module downgraded MetadataReferences
    /// from previous analyze calls. Seeded into the local working dict so
    /// changed modules can see UNCHANGED dependent modules' PE images during
    /// compilation. After the call returns, every module the builder
    /// processed (or carried forward) has its current PE-image reference
    /// merged back into the same dict. Pass <c>null</c> to start fresh.
    /// INV-INCREMENTAL-XREF-001.</param>
    public Dictionary<string, CSharpCompilation>? ProcessInOrder(
        ModuleInfo[] modules,
        string projectRoot,
        AnalysisConfig config,
        CompilationProcessor processor,
        Action<string, int, int>? onModuleProgress = null,
        List<SkippedFile>? skippedCollector = null,
        Dictionary<string, MetadataReference>? carryDowngraded = null)
    {
        var sorted = TopologicalSort(modules);
        var moduleLookup = modules.ToDictionary(m => m.Name, StringComparer.Ordinal);

        // Downgraded references: lightweight PE images for completed modules.
        // Downstream modules reference these instead of full compilations.
        // INV-INCREMENTAL-XREF-001: seed from the snapshot-owned carry so
        // changed modules can see UNCHANGED dependent modules' metadata
        // refs during incremental re-analyze. Without this, cross-module
        // symbol bindings collapse and the edges are silently dropped.
        var downgraded = carryDowngraded != null
            ? new Dictionary<string, MetadataReference>(carryDowngraded, StringComparer.Ordinal)
            : new Dictionary<string, MetadataReference>(StringComparer.Ordinal);

        // Only allocated when retaining for write-side tools.
        Dictionary<string, CSharpCompilation>? retained = config.RetainCompilations
            ? new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
            : null;

        for (int i = 0; i < sorted.Length; i++)
        {
            var module = sorted[i];
            onModuleProgress?.Invoke(module.Name, i + 1, sorted.Length);

            // Reference closure mode is a discovered module fact
            // (INV-MODULE-REFS-001). Two semantics in one builder:
            //
            //   Transitive (SDK-style MSBuild, Lifeblood self): A → B → C
            //     pulls C into A's compile classpath. Required so types from
            //     C reachable through B's public surface bind in A's source
            //     — without C as a ref, the type becomes an error symbol
            //     with an empty ContainingNamespace and every derived
            //     method id loses its namespace qualifier, silently
            //     producing non-canonical IDs (NEW-01 / INV-CANONICAL-001).
            //
            //   DirectOnly (Unity asmdef, old-format MSBuild 2003-schema
            //     csprojs): A → B does NOT pull B's other refs onto A's
            //     classpath. Mirrors Unity's behavior where every asmdef
            //     must explicitly list each direct AND transitively-exposed
            //     assembly; a workspace that compiles in Unity provably
            //     never has transitively-exposed types in any module's
            //     source. Pulling them in anyway exposes sibling-namespace
            //     assemblies (e.g. <c>Acme.Math.dll</c>) to lookup, where
            //     they shadow BCL types — bare <c>Math.Min</c> in
            //     <c>namespace Acme.X</c> binds to <c>Acme.Math</c>
            //     namespace and emits a spurious CS0234. INV-MODULE-REFS-001.
            //
            // The topological sort above orders modules so every direct or
            // transitive dependency of `module` has been compiled and
            // downgraded by the time we read `downgraded` here, regardless
            // of which closure mode we use to filter it.
            var depRefs = (module.ReferenceClosure == ReferenceClosureMode.DirectOnly
                    ? (IEnumerable<string>)module.Dependencies
                    : ComputeTransitiveDependencies(module, moduleLookup))
                .Where(downgraded.ContainsKey)
                .Select(d => downgraded[d])
                .ToArray();

            var compilation = CreateCompilation(module, projectRoot, config, depRefs, skippedCollector);
            if (compilation == null) continue;

            // Invoke the processor (symbol/edge extraction happens here).
            processor(module, compilation);

            // Retain full compilation if write-side tools are needed.
            if (retained != null)
                retained[module.Name] = compilation;

            // Downgrade: emit to PE bytes → lightweight MetadataReference.
            // Downstream modules only need the type metadata, not the full compilation.
            var downgradedRef = DowngradeCompilation(compilation);
            downgraded[module.Name] = downgradedRef;
        }

        // INV-INCREMENTAL-XREF-001: merge the working dict back into the
        // caller-owned carry so subsequent incremental calls inherit fresh
        // PE refs for modules we just (re)processed AND preserve refs for
        // modules we carried forward without touching. Module removals are
        // handled by the caller (RoslynWorkspaceAnalyzer) when it observes
        // a deleted module — `carryDowngraded` is the snapshot's persistent
        // mirror and only the snapshot owner knows when a module is gone.
        if (carryDowngraded != null)
        {
            foreach (var (name, mref) in downgraded)
            {
                carryDowngraded[name] = mref;
            }
        }

        return retained;
    }

    /// <summary>
    /// Emit the compilation to a PE image and wrap it as a MetadataReference.
    /// Falls back to ToMetadataReference() if emit fails (compilation errors).
    /// The fallback keeps the full compilation alive — acceptable for the few
    /// modules that have errors, while most modules get the memory savings.
    /// </summary>
    private static MetadataReference DowngradeCompilation(CSharpCompilation compilation)
    {
        try
        {
            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (emitResult.Success)
                return MetadataReference.CreateFromImage(ms.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BadImageFormatException)
        {
            // Emit can throw for pathological compilations. Fall back gracefully.
        }

        // Fallback: wrap the in-memory compilation. More expensive but correct.
        return compilation.ToMetadataReference();
    }

    private CSharpCompilation? CreateCompilation(
        ModuleInfo module, string projectRoot, AnalysisConfig config,
        MetadataReference[] dependencyRefs,
        List<SkippedFile>? skippedCollector)
    {
        // Surface every file the adapter declines to process so consumers
        // can show users exactly what was dropped and why.
        // Order matters: extension filter runs first, then file-existence,
        // so a missing non-.cs file is reported as UnsupportedExtension
        // (the dominant reason) rather than FileNotFound.
        var sourceFiles = module.FilePaths
            .Where(f =>
            {
                if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    skippedCollector?.Add(new SkippedFile
                    {
                        FilePath = f,
                        Reason = SkipReason.UnsupportedExtension,
                        ModuleName = module.Name,
                    });
                    return false;
                }
                return true;
            })
            .Where(f =>
            {
                if (!_fs.FileExists(f))
                {
                    skippedCollector?.Add(new SkippedFile
                    {
                        FilePath = f,
                        Reason = SkipReason.FileNotFound,
                        ModuleName = module.Name,
                    });
                    return false;
                }
                return true;
            });

        if (config.ExcludePatterns.Length > 0)
        {
            sourceFiles = sourceFiles.Where(f =>
            {
                var rel = Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
                return !config.ExcludePatterns.Any(p => rel.Contains(p, StringComparison.OrdinalIgnoreCase));
            });
        }

        // Preprocessor symbols are a discovered module fact (INV-COMPFACT-001..003
        // + INV-DIAGNOSTIC-ENVELOPE-DEFINES-001). When the csproj declares
        // <DefineConstants>X;Y</DefineConstants>, every #if-guarded block whose
        // token matches X or Y must be included in the compilation unit;
        // otherwise symbols referenced only inside those guards become
        // invisible to find_references / dead_code / blast_radius (the
        // empirical L-LIM-001 trap). Roslyn's primitive is
        // CSharpParseOptions.WithPreprocessorSymbols — pass it to ParseText
        // so every #if token resolves consistently across the module.
        //
        // When the module's symbol set is empty we still pass an explicit
        // CSharpParseOptions instance instead of the default-null so
        // RoslynCompilationHost.GetActiveDefines reads the canonical
        // PreprocessorSymbolNames property (empty array) rather than the
        // "options is null" fallback. Wire shape stays uniform.
        var parseOptions = CSharpParseOptions.Default;
        if (module.PreprocessorSymbols.Length > 0)
            parseOptions = parseOptions.WithPreprocessorSymbols(module.PreprocessorSymbols);

        // <LangVersion> (LB-FOLLOWUP-001). Roslyn ships a canonical parser
        // for the csproj string form; use it instead of a homegrown lookup
        // so "latest" / "preview" / "12" / "11.0" / "7.3" all bind to the
        // same enum the official compiler reaches.
        if (!string.IsNullOrEmpty(module.LanguageVersion)
            && LanguageVersionFacts.TryParse(module.LanguageVersion, out var langVersion))
        {
            parseOptions = parseOptions.WithLanguageVersion(langVersion);
        }

        // <Features> (INV-RUNTIME-ASYNC-COMPAT-001). Preserve compiler
        // feature switches declared by the analyzed project, including
        // preview markers such as runtime-async=on. Lifeblood does not
        // interpret or enable these for itself; it threads the project fact
        // into Roslyn so analyze/diagnose/compile_check see the same parse
        // options as the user's build.
        if (module.CompilerFeatures.Count > 0)
            parseOptions = (CSharpParseOptions)parseOptions.WithFeatures(module.CompilerFeatures);

        var trees = sourceFiles
            .Select(f =>
            {
                try { return CSharpSyntaxTree.ParseText(_fs.ReadAllText(f), parseOptions, path: f); }
                catch (IOException) { return null; }
            })
            .Where(t => t != null)
            .Select(t => t!)  // non-null proven by the Where above; strips the nullable flow annotation so downstream sees SyntaxTree[], not SyntaxTree?[]
            .ToArray();

        if (trees.Length == 0) return null;

        // BCL ownership is a discovered module fact. Read the field, act on it.
        // Do not re-derive from ExternalDllPaths, do not sniff filenames here —
        // detection lives in RoslynModuleDiscovery, single source of truth.
        // See INV-BCL-001..INV-BCL-004 in .claude/plans/bcl-ownership-fix.md
        // for the failure mode this prevents (CS0433/CS0518 → null GetSymbolInfo
        // → silent zero results from find_references / dependants / call-graph).
        var references = module.BclOwnership == BclOwnershipMode.ModuleProvided
            ? new List<MetadataReference>()
            : new List<MetadataReference>(BclReferenceLoader.References.Value);
        references.AddRange(_nuget.Resolve(module, projectRoot, _refCache));
        references.AddRange(dependencyRefs);

        // Load external DLLs referenced via HintPath (e.g., Unity engine assemblies).
        // Uses the shared cache to deduplicate — 100 modules sharing UnityEngine.CoreModule.dll
        // produce a single MetadataReference, not 100 independent copies.
        foreach (var dllPath in module.ExternalDllPaths)
        {
            try
            {
                if (!BclReferenceLoader.IsNativeDll(dllPath))
                    references.Add(_refCache.GetOrCreate(dllPath));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        }

        // Compilation options follow discovered module facts (INV-COMPFACT-001..003).
        // Each csproj-driven option lives as a typed field on ModuleInfo, set
        // once during discovery, consumed exactly once here. NEVER re-derive
        // from the csproj at this layer; NEVER sniff filenames as a substitute.
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAllowUnsafe(module.AllowUnsafeCode)
            .WithNullableContextOptions(ParseNullableContext(module.NullableContext));

        // <NoWarn> (LB-FOLLOWUP-003). Csproj declares the IDs the build
        // should suppress; mirror that in the Roslyn compilation so
        // diagnose responses don't surface noise the caller already
        // silenced. Each ID gets ReportDiagnostic.Suppress — Roslyn's
        // canonical primitive for per-ID severity overrides.
        if (module.NoWarnDiagnosticIds.Length > 0)
        {
            var noWarnDict = new Dictionary<string, ReportDiagnostic>(
                module.NoWarnDiagnosticIds.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var id in module.NoWarnDiagnosticIds)
                noWarnDict[id] = ReportDiagnostic.Suppress;
            compilationOptions = compilationOptions.WithSpecificDiagnosticOptions(noWarnDict);
        }

        // Implicit global usings (INV-COMPFACT-001..003). When the csproj declares
        // <ImplicitUsings>enable</ImplicitUsings>, MSBuild generates global usings
        // for the standard namespaces. We inject the same set as a synthetic tree
        // because we compile from source, not through MSBuild.
        //
        // <InternalsVisibleTo Include="X"> items (INV-DIAGNOSTIC-IVT-PARITY-001).
        // MSBuild's GenerateAssemblyInfo target writes one
        // [assembly: InternalsVisibleTo("X")] per item into
        // obj/<Tfm>/<AssemblyName>.AssemblyInfo.cs; Lifeblood's SDK-style scan
        // skips obj/ so we synthesize the equivalent attribute tree here.
        // Parsed with the module's parse options so AddSyntaxTrees /
        // ReplaceSyntaxTree downstream do not throw "Inconsistent language
        // versions" when the module declares a non-default LangVersion.
        var allTrees = trees;
        if (module.ImplicitUsings)
            allTrees = allTrees.Append(BuildImplicitGlobalUsingsTree(parseOptions)).ToArray();
        if (module.InternalsVisibleTo.Length > 0)
            allTrees = allTrees.Append(BuildInternalsVisibleToTree(module.InternalsVisibleTo, parseOptions)).ToArray();

        // Assembly identity unification (INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001).
        // Without this, a module whose NuGet closure brings in System.* contract
        // assemblies that overlap simple-names with the SDK BCL ref pack (the
        // canonical xunit / Microsoft.NET.Test.Sdk / xunit.runner.visualstudio
        // shape) makes Roslyn emit CS1701 / CS1702 / CS1705 once per type-ref —
        // empirically 7,537 spurious findings on Lifeblood's own test assembly
        // while MSBuild was clean on the same compilation. The fix mirrors
        // MSBuild's `<AutoUnify>true</AutoUnify>` default: collapse to one ref
        // per simple-name keeping the highest version. No per-name special case.
        var dedupedReferences = MetadataReferenceDeduplicator.Deduplicate(references);

        var compilation = CSharpCompilation.Create(
            module.Name,
            allTrees,
            dedupedReferences,
            compilationOptions);

        return SourceGeneratorRunner.Run(compilation, module.SourceGeneratorAnalyzerPaths, module);
    }

    /// <summary>
    /// Build the synthetic syntax tree that mirrors MSBuild's
    /// <c>GenerateAssemblyInfo</c> output for <c>&lt;InternalsVisibleTo
    /// Include="X" /&gt;</c> item-group entries. One
    /// <c>[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("X")]</c>
    /// per declared target, fully-qualified so the tree compiles regardless
    /// of which usings the module's source declares. Parsed with the
    /// module's own <see cref="CSharpParseOptions"/> so downstream
    /// <c>AddSyntaxTrees</c> / <c>ReplaceSyntaxTree</c> calls do not throw
    /// "Inconsistent language versions" against a module that declared a
    /// non-default <c>&lt;LangVersion&gt;</c>. INV-DIAGNOSTIC-IVT-PARITY-001.
    /// </summary>
    private static SyntaxTree BuildInternalsVisibleToTree(string[] targets, CSharpParseOptions parseOptions)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var target in targets)
            sb.Append("[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"").Append(target).AppendLine("\")]");
        return CSharpSyntaxTree.ParseText(sb.ToString(), parseOptions, path: "<InternalsVisibleTo>.cs");
    }

    /// <summary>
    /// Build the synthetic tree that mirrors MSBuild's implicit global-usings
    /// output. Parse with the module's own <see cref="CSharpParseOptions"/>
    /// because Roslyn requires every tree in a compilation to carry the same
    /// feature set. This is load-bearing for preview compiler switches such as
    /// <c>&lt;Features&gt;runtime-async=on&lt;/Features&gt;</c>.
    /// </summary>
    private static SyntaxTree BuildImplicitGlobalUsingsTree(CSharpParseOptions parseOptions)
        => CSharpSyntaxTree.ParseText(
            ImplicitGlobalUsingsSource,
            parseOptions,
            path: "<ImplicitGlobalUsings>.cs");

    /// <summary>
    /// Map the csproj <c>&lt;Nullable&gt;</c> string value to Roslyn's
    /// <see cref="NullableContextOptions"/>. Empty / unrecognized values fall
    /// back to <see cref="NullableContextOptions.Disable"/> — the same
    /// default Roslyn applies to a project that declares no Nullable
    /// property. INV-COMPFACT-001..003 / LB-FOLLOWUP-002.
    /// </summary>
    private static NullableContextOptions ParseNullableContext(string raw)
        => raw.ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };

    /// <summary>
    /// Compute the transitive closure of module dependencies. Given module A
    /// with direct Dependencies [B], and B with direct Dependencies [C],
    /// returns {B, C}. Direct-only dependency lists are NOT sufficient for
    /// Roslyn compilation references: Roslyn needs every assembly whose types
    /// appear in the module's source, including assemblies reached transitively
    /// through another reference's public surface. Missing transitive refs
    /// silently produce error type symbols with empty namespaces, which in
    /// turn produce non-canonical symbol IDs during extraction.
    ///
    /// Cycles are handled gracefully — every visited module is added once and
    /// not revisited. Modules missing from the lookup (malformed dependency
    /// names) are silently skipped. The order of the returned set is not
    /// significant; the caller filters it against the `downgraded` map.
    /// </summary>
    internal static HashSet<string> ComputeTransitiveDependencies(
        ModuleInfo module,
        Dictionary<string, ModuleInfo> lookup)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(module.Dependencies);
        while (stack.Count > 0)
        {
            var name = stack.Pop();
            if (!closure.Add(name)) continue;
            if (!lookup.TryGetValue(name, out var dep)) continue;
            foreach (var transitive in dep.Dependencies)
            {
                if (!closure.Contains(transitive))
                    stack.Push(transitive);
            }
        }
        return closure;
    }

    /// <summary>
    /// Topological sort: returns modules in dependency-first order.
    /// Handles cycles gracefully — breaks them by processing what's available.
    /// </summary>
    internal static ModuleInfo[] TopologicalSort(ModuleInfo[] modules)
    {
        var lookup = modules.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var visited = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new List<ModuleInfo>();

        foreach (var module in modules)
        {
            if (!visited.ContainsKey(module.Name))
                Visit(module, lookup, visited, result);
        }

        return result.ToArray();
    }

    private static void Visit(
        ModuleInfo module,
        Dictionary<string, ModuleInfo> lookup,
        Dictionary<string, bool> visited,
        List<ModuleInfo> result)
    {
        if (visited.TryGetValue(module.Name, out var permanent))
        {
            if (permanent) return;
            return; // cycle detected — break by skipping
        }

        visited[module.Name] = false;

        foreach (var dep in module.Dependencies)
        {
            if (lookup.TryGetValue(dep, out var depModule))
                Visit(depModule, lookup, visited, result);
        }

        visited[module.Name] = true;
        result.Add(module);
    }
}
