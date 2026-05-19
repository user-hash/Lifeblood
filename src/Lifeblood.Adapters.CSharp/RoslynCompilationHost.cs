using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DomainDiagnosticSeverity = Lifeblood.Domain.Results.DiagnosticSeverity;
using DomainReferenceLocation = Lifeblood.Domain.Results.ReferenceLocation;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Roslyn-backed compilation host. Provides diagnostics, compile-checking, and reference finding.
/// Built from retained compilations after workspace analysis.
/// </summary>
public sealed class RoslynCompilationHost : ICompilationHost, Internal.IRoslynLookup, IDisposable
{
  private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
  private readonly Lazy<RoslynWorkspaceManager> _manager;

  public RoslynCompilationHost(
  IReadOnlyDictionary<string, CSharpCompilation> compilations,
  IReadOnlyDictionary<string, string[]>? moduleDependencies = null)
  {
  _compilations = compilations;
  _manager = new Lazy<RoslynWorkspaceManager>(
  () => new RoslynWorkspaceManager(compilations, moduleDependencies));
  }

  public bool IsAvailable => _compilations.Count > 0;

  /// <summary>
  /// S8a: <see cref="Internal.IRoslynLookup"/> implementation so
  /// extracted services depend on the interface, not the concrete
  /// host type. Eliminates the type-cycle the direct-host-reference
  /// design created — INV-ADAPTER-THIN-001 / INV-ADAPTER-LOOKUP-SEAM-001.
  /// </summary>
  IReadOnlyDictionary<string, CSharpCompilation> Internal.IRoslynLookup.Compilations => _compilations;

  /// <summary>
  /// Instance forwarder for the static <see cref="BuildSymbolId"/>
  /// helper. Required so <see cref="Internal.IRoslynLookup"/> can
  /// expose the canonical-id builder as a mockable instance member
  /// without changing the static call sites scattered through the
  /// host.
  /// </summary>
  string Internal.IRoslynLookup.BuildSymbolId(ISymbol symbol) => BuildSymbolId(symbol);

  /// <summary>
  /// Instance forwarder for <see cref="ResolveFromSource"/> exposed
  /// through <see cref="Internal.IRoslynLookup"/>. Explicit interface
  /// implementation so the host's direct callers (within this file)
  /// still use the simpler non-interface entry point.
  /// </summary>
  ISymbol? Internal.IRoslynLookup.ResolveFromSource(string symbolId) => ResolveFromSource(symbolId);

  public DiagnosticInfo[] GetDiagnostics(string? moduleName = null) =>
  GetDiagnostics(new DiagnosticsRequest { ModuleName = moduleName });

  public DiagnosticsReport GetDiagnosticsReport(DiagnosticsRequest request)
  {
  // File-scope: route through FindOwningCompilation (same path-match
  // rules as the rest of the adapter). Module-scope: use the request's
  // module name verbatim. Project-wide: empty resolvedModule, defines
  // are the sorted-deduped union across every compilation.
  // INV-DIAGNOSTIC-ENVELOPE-DEFINES-001.
  string resolvedModule = request.ModuleName ?? "";
  if (!string.IsNullOrEmpty(request.FilePath) && string.IsNullOrEmpty(resolvedModule))
  {
  var owning = FindOwningCompilation(request.FilePath!, null);
  resolvedModule = owning.Module ?? "";
  }

  return new DiagnosticsReport
  {
  Diagnostics = GetDiagnostics(request),
  DefinesActive = CollectDefines(string.IsNullOrEmpty(resolvedModule) ? null : resolvedModule),
  ResolvedModule = resolvedModule,
  };
  }

  public DiagnosticInfo[] GetDiagnostics(DiagnosticsRequest request)
  {
  var moduleName = request.ModuleName;
  var requestedFile = request.FilePath;

  if (moduleName != null && !_compilations.ContainsKey(moduleName))
  return Array.Empty<DiagnosticInfo>();

  var results = new List<DiagnosticInfo>();

  var compilations = moduleName != null && _compilations.TryGetValue(moduleName, out var single)
  ? new[] { (name: moduleName, comp: single) }
  : _compilations.Select(kv => (name: kv.Key, comp: kv.Value)).ToArray();

  foreach (var (name, compilation) in compilations)
  {
  foreach (var diag in compilation.GetDiagnostics())
  {
  if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden) continue;
  // Skip Info-level diagnostics by default (noise from nullable contexts, etc.)
  if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Info) continue;

  var lineSpan = diag.Location.GetMappedLineSpan();

  // File scope filter: restrict to diagnostics whose syntax-tree path
  // matches the requested file. Match end-of-path so callers can pass
  // a relative form (e.g. "src/Foo/Bar.cs") and have it match the
  // compilation's stored absolute path. Comparison is case-insensitive
  // (Windows-friendly) on the path-separator-normalized form.
  if (requestedFile != null && !PathsMatch(lineSpan.Path, requestedFile))
  continue;

  results.Add(new DiagnosticInfo
  {
  Id = diag.Id,
  Message = diag.GetMessage(),
  Severity = MapSeverity(diag.Severity),
  FilePath = lineSpan.Path ?? "",
  Line = lineSpan.StartLinePosition.Line + 1,
  Column = lineSpan.StartLinePosition.Character + 1,
  Module = name,
  });
  }
  }

  return results.ToArray();
  }

  /// <summary>
  /// Active preprocessor symbols for the resolved scope. Pulls
  /// <c>CSharpParseOptions.PreprocessorSymbolNames</c> off the chosen
  /// compilation's parse options; for project-wide scope (null
  /// <paramref name="moduleName"/>), returns the union across every
  /// loaded compilation. Always sorted ASCII-ordinal and deduplicated
  /// so the wire form is stable across analyze runs. Skips
  /// compilations whose parse options are not <c>CSharpParseOptions</c>
  /// (defensive — every Roslyn C# compilation carries them in
  /// practice). INV-DIAGNOSTIC-ENVELOPE-DEFINES-001.
  /// </summary>
  private string[] CollectDefines(string? moduleName)
  {
  IEnumerable<CSharpCompilation> targets;
  if (moduleName != null)
  {
  if (!_compilations.TryGetValue(moduleName, out var pinned))
  return Array.Empty<string>();
  targets = new[] { pinned };
  }
  else
  {
  targets = _compilations.Values;
  }

  var defines = new SortedSet<string>(StringComparer.Ordinal);
  foreach (var compilation in targets)
  {
  // ParseOptions on a CSharpCompilation is the project-level
  // CSharpParseOptions; each SyntaxTree may override but in
  // practice asmdef-generated modules carry one define set across
  // every tree. Use the compilation's options for the canonical
  // "what defines did Lifeblood compile this module with" answer.
  if (compilation.SyntaxTrees.Length == 0) continue;
  var opts = compilation.SyntaxTrees[0].Options as CSharpParseOptions;
  if (opts == null) continue;
  foreach (var sym in opts.PreprocessorSymbolNames)
  if (!string.IsNullOrEmpty(sym)) defines.Add(sym);
  }
  return defines.ToArray();
  }

  /// <summary>
  /// True if <paramref name="diagPath"/> (the absolute path Roslyn reports
  /// for a diagnostic location) matches <paramref name="userPath"/> (the
  /// caller-supplied scope filter, which may be relative or absolute).
  /// Both forms are normalized to forward-slashes and lowercased before
  /// the suffix comparison; either side may be the suffix of the other so
  /// passing the absolute path or the project-relative path both work.
  /// Returns false for null/empty diagnostic paths.
  /// </summary>
  private static bool PathsMatch(string? diagPath, string userPath)
  {
  if (string.IsNullOrEmpty(diagPath)) return false;
  var a = diagPath.Replace('\\', '/').ToLowerInvariant();
  var b = userPath.Replace('\\', '/').ToLowerInvariant();
  if (a == b) return true;
  if (a.EndsWith("/" + b, StringComparison.Ordinal)) return true;
  if (b.EndsWith("/" + a, StringComparison.Ordinal)) return true;
  return false;
  }

  public CompileCheckResult CompileCheck(string code, string? moduleName = null)
  => CompileCheck(new CompileCheckRequest { Code = code, ModuleName = moduleName });

  public CompileCheckResult CompileCheck(CompileCheckRequest request)
  {
  // File-mode: caller passed a path. Auto-detect the owning module
  // by matching the path against every compilation's syntax-tree
  // paths, then SWAP the existing tree for fresh source. This is the
  // only correct edit-then-check semantics for files that are
  // already part of the workspace — adding the same file as a NEW
  // tree (the legacy snippet path) duplicates every type declaration
  // and emits CS0101 / CS0102 on first compile.
  if (!string.IsNullOrEmpty(request.FilePath))
  {
  return CompileCheckFile(request.FilePath!, request.Code, request.ModuleName);
  }

  // Snippet-mode: legacy behavior. Pick the requested module (or the
  // first compilation), wrap statements as a method body, ADD as a
  // new tree, emit, filter to NEW diagnostics.
  return CompileCheckSnippet(request.Code ?? string.Empty, request.ModuleName);
  }

  private CompileCheckResult CompileCheckFile(string filePath, string? overrideCode, string? moduleName)
  {
  // Find the owning compilation. If the caller pinned a moduleName,
  // require the file to live in that module; otherwise scan every
  // compilation for a matching syntax-tree path.
  var (resolvedModule, owningCompilation, existingTree) =
  FindOwningCompilation(filePath, moduleName);
  if (owningCompilation == null)
  {
  return new CompileCheckResult
  {
  Success = false,
  Diagnostics = new[] { new DiagnosticInfo
  {
  Id = "LB0002",
  Message = moduleName != null
  ? $"File '{filePath}' not found in module '{moduleName}'. Pass moduleName=null to auto-detect."
  : $"File '{filePath}' not found in any loaded compilation. Did the analyze step include this module? " +
  $"Compilations available: {string.Join(", ", _compilations.Keys)}.",
  Severity = DomainDiagnosticSeverity.Error,
  }},
  ResolvedModule = resolvedModule ?? "",
  };
  }

  // Source: explicit override (rare — caller knows file content
  // differs from disk) or the existing tree's text. We deliberately
  // do NOT read off disk here — the handler already routed through
  // IFileSystem and may have applied a stale-refresh.
  string newSource;
  if (!string.IsNullOrEmpty(overrideCode))
  {
  newSource = overrideCode!;
  }
  else if (existingTree != null)
  {
  newSource = existingTree.GetText().ToString();
  }
  else
  {
  return new CompileCheckResult
  {
  Success = false,
  Diagnostics = new[] { new DiagnosticInfo
  {
  Id = "LB0003",
  Message = $"File '{filePath}' resolved to module '{resolvedModule}' but had no existing tree and no inline 'code' override; nothing to compile-check.",
  Severity = DomainDiagnosticSeverity.Error,
  }},
  ResolvedModule = resolvedModule ?? "",
  };
  }

  // Build the replacement tree at the SAME path so diagnostics keep
  // pointing at the user's file, not a synthetic snippet path. Thread
  // the owning module's CSharpParseOptions (LangVersion, Nullable,
  // PreprocessorSymbols — set per INV-COMPFACT-001..003 in
  // ModuleCompilationBuilder) so AddSyntaxTrees / ReplaceSyntaxTree
  // does not throw "Inconsistent language versions" when the module
  // declares a non-default LangVersion.
  var preservedPath = existingTree?.FilePath ?? filePath;
  var moduleParseOptions = GetModuleParseOptions(owningCompilation);
  var newTree = CSharpSyntaxTree.ParseText(newSource, moduleParseOptions, path: preservedPath);

  // Pre-existing diagnostics computed against the unswapped
  // compilation so we don't surface errors that were already present
  // in OTHER files in this module — only changes the user introduced
  // in THIS file (or net-new errors caused by the swap) are reported.
  var preExistingIds = new HashSet<string>(
  owningCompilation.GetDiagnostics()
  .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
  .Select(d => DiagnosticKey(d)));

  var testCompilation = existingTree != null
  ? owningCompilation.ReplaceSyntaxTree(existingTree, newTree)
  : owningCompilation.AddSyntaxTrees(newTree);

  using var ms = new MemoryStream();
  var emitResult = testCompilation.Emit(ms);

  var diagnostics = emitResult.Diagnostics
  .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
  .Where(d => !preExistingIds.Contains(DiagnosticKey(d)))
  .Select(d =>
  {
  var lineSpan = d.Location.GetMappedLineSpan();
  return new DiagnosticInfo
  {
  Id = d.Id,
  Message = d.GetMessage(),
  Severity = MapSeverity(d.Severity),
  FilePath = lineSpan.Path ?? "",
  Line = lineSpan.StartLinePosition.Line + 1,
  Column = lineSpan.StartLinePosition.Character + 1,
  Module = resolvedModule,
  };
  })
  .ToArray();

  var hasErrors = diagnostics.Any(d => d.Severity == DomainDiagnosticSeverity.Error);

  return new CompileCheckResult
  {
  Success = !hasErrors,
  Diagnostics = diagnostics,
  ResolvedModule = resolvedModule ?? "",
  ExistingTreeReplaced = existingTree != null,
  DefinesActive = CollectDefines(resolvedModule),
  };
  }

  private CompileCheckResult CompileCheckSnippet(string code, string? moduleName)
  {
  // Route through ResolveCompilation so the snippet binds against a
  // named compilation and DefinesActive reflects that module's
  // parse-options symbol set. INV-DIAGNOSTIC-ENVELOPE-DEFINES-001.
  var (resolvedModuleName, targetCompilation) = ResolveCompilation(moduleName);
  if (targetCompilation == null)
  return new CompileCheckResult
  {
  Success = false,
  Diagnostics = new[] { new DiagnosticInfo
  {
  Id = "LB0001",
  Message = moduleName != null
  ? $"Module '{moduleName}' not found. Available: {string.Join(", ", _compilations.Keys)}"
  : "No compilations available.",
  Severity = DomainDiagnosticSeverity.Error,
  }},
  };

  var preExistingIds = new HashSet<string>(
  targetCompilation.GetDiagnostics()
  .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
  .Select(d => DiagnosticKey(d)));

  // Snippet preparation (auto-wrap statements as a method body so library
  // modules accept them — see Internal.SnippetWrapper for the contract).
  // The wrapper preserves diagnostic line numbers via MapLineToUser so the
  // user sees errors at the line they typed, not at the synthetic wrapper.
  var prepared = Internal.SnippetWrapper.Prepare(code, GetModuleParseOptions(targetCompilation));
  var testCompilation = targetCompilation.AddSyntaxTrees(prepared.Tree);

  using var ms = new MemoryStream();
  var emitResult = testCompilation.Emit(ms);

  var snippetDiagnostics = emitResult.Diagnostics
  .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
  .Where(d => !preExistingIds.Contains(DiagnosticKey(d)))
  .Select(d =>
  {
  var lineSpan = d.Location.GetMappedLineSpan();
  var rawLine = lineSpan.StartLinePosition.Line + 1;
  return new DiagnosticInfo
  {
  Id = d.Id,
  Message = d.GetMessage(),
  Severity = MapSeverity(d.Severity),
  FilePath = lineSpan.Path ?? "",
  Line = Internal.SnippetWrapper.MapLineToUser(in prepared, rawLine),
  Column = lineSpan.StartLinePosition.Character + 1,
  };
  })
  .ToArray();

  var hasNewErrors = snippetDiagnostics.Any(d => d.Severity == DomainDiagnosticSeverity.Error);

  return new CompileCheckResult
  {
  Success = !hasNewErrors,
  Diagnostics = snippetDiagnostics,
  ResolvedModule = resolvedModuleName ?? "",
  DefinesActive = CollectDefines(resolvedModuleName),
  };
  }

  /// <summary>
  /// Return the <see cref="CSharpParseOptions"/> the owning compilation
  /// parses its source trees under. compile_check must parse replacement
  /// + snippet trees with these same options so `AddSyntaxTrees` /
  /// `ReplaceSyntaxTree` does not throw "Inconsistent language versions"
  /// when the module declares non-default `LangVersion` / `Nullable`
  /// / `DefineConstants` (set per INV-COMPFACT-001..003 in
  /// `ModuleCompilationBuilder`). Returns null when the compilation is
  /// empty or carries only synthetic trees (paths starting with `&lt;`),
  /// in which case `CSharpSyntaxTree.ParseText` falls back to defaults
  /// — the same behavior as before INV-COMPFACT thread-through.
  /// </summary>
  private static CSharpParseOptions? GetModuleParseOptions(CSharpCompilation compilation)
  {
  foreach (var tree in compilation.SyntaxTrees)
  {
  // Skip synthetic trees (e.g. `<ImplicitGlobalUsings>.cs`) — those
  // are parsed with default options and don't represent module facts.
  if (!string.IsNullOrEmpty(tree.FilePath) && tree.FilePath.StartsWith("<")) continue;
  return tree.Options as CSharpParseOptions;
  }
  return null;
  }

  private static string DiagnosticKey(Diagnostic d)
  {
  var span = d.Location.GetMappedLineSpan();
  return $"{d.Id}:{span.Path}:{span.StartLinePosition.Line}";
  }

  /// <summary>
  /// Walk every loaded compilation for the syntax tree whose path
  /// matches <paramref name="filePath"/>. Match is case-insensitive
  /// forward-slash suffix: a tree at <c><project-root>/Assets/.../Foo.cs</c>
  /// resolves a request for <c>Assets/.../Foo.cs</c>, an absolute
  /// path, or anything in between. When <paramref name="moduleName"/>
  /// is set the search is restricted to that one compilation.
  /// </summary>
  private (string? Module, CSharpCompilation? Compilation, SyntaxTree? Tree) FindOwningCompilation(string filePath, string? moduleName)
  {
  var normalized = filePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

  IEnumerable<KeyValuePair<string, CSharpCompilation>> candidates =
  moduleName != null && _compilations.TryGetValue(moduleName, out var pinned)
  ? new[] { new KeyValuePair<string, CSharpCompilation>(moduleName, pinned) }
  : _compilations;

  foreach (var kv in candidates)
  {
  foreach (var tree in kv.Value.SyntaxTrees)
  {
  var treePath = (tree.FilePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
  if (treePath.Length == 0) continue;
  if (treePath == normalized) return (kv.Key, kv.Value, tree);
  if (treePath.EndsWith("/" + normalized, StringComparison.Ordinal)) return (kv.Key, kv.Value, tree);
  if (normalized.EndsWith("/" + treePath, StringComparison.Ordinal)) return (kv.Key, kv.Value, tree);
  }
  }

  return (moduleName, null, null);
  }

  public DomainReferenceLocation[] FindReferences(string symbolId)
  => FindReferences(symbolId, FindReferencesOptions.Default);

  public DomainReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options)
      => new RoslynFindReferencesService((Internal.IRoslynLookup)this).FindReferences(symbolId, options);

  /// <summary>
  /// Resolve a symbol ID, preferring the source-defined copy over metadata copies.
  /// In a multi-assembly workspace, the same symbol exists as source in its home module
  /// and as metadata in every consumer. The source copy has the canonical display string,
  /// source locations, and XML documentation. Metadata copies have none of these.
  /// </summary>
  internal ISymbol? ResolveFromSource(string symbolId)
  {
  var mgr = _manager.Value;
  var roslynSymbol = mgr.ResolveSymbol(symbolId, fallbackToStandalone: true);
  if (roslynSymbol != null && IsFromSource(roslynSymbol)) return roslynSymbol;

  // Workspace returned metadata copy. Search standalone compilations for source
  var parsed = RoslynWorkspaceManager.ParseSymbolId(symbolId);
  if (parsed.Kind == null || parsed.Parts == null) return roslynSymbol;

  foreach (var compilation in _compilations.Values)
  {
  var found = RoslynWorkspaceManager.FindInCompilation(compilation, parsed);
  if (found != null && IsFromSource(found)) return found;
  }
  return roslynSymbol; // metadata fallback. Better than null
  }

  /// <summary>
  /// Returns true if the symbol has at least one source location (not metadata-only).
  /// </summary>
  private static bool IsFromSource(ISymbol symbol)
  => symbol.Locations.Any(l => l.IsInSource);

  public DefinitionLocation? FindDefinition(string symbolId)
  {
  // Resolve with source preference. A cross-assembly symbol may resolve to its
  // metadata copy first (no source location, no XML docs). The source-defined copy
  // in the home compilation has the actual file/line/documentation.
  var roslynSymbol = ResolveFromSource(symbolId);
  if (roslynSymbol == null) return null;

  var location = roslynSymbol.Locations.FirstOrDefault(l => l.IsInSource);
  if (location == null) return null;

  var lineSpan = location.GetMappedLineSpan();
  var doc = GetXmlDocumentation(roslynSymbol);

  return new DefinitionLocation
  {
  SymbolId = symbolId,
  FilePath = lineSpan.Path ?? "",
  Line = lineSpan.StartLinePosition.Line + 1,
  Column = lineSpan.StartLinePosition.Character + 1,
  DisplayName = roslynSymbol.ToDisplayString(),
  Documentation = doc,
  };
  }

  /// <summary>
  /// Per-member enum coverage. Resolves <paramref name="enumTypeId"/>
  /// to a source enum, builds a canonical-id → counter map for every
  /// declared member, then walks every loaded compilation's syntax
  /// trees ONCE and classifies each member reference by inspecting
  /// the parent syntax. INV-ENUM-COVERAGE-001 /
  /// LB-TRACK-20260514-003. Returns null when the type does not
  /// resolve or is not an enum.
  /// </summary>
  public EnumCoverageReport? GetEnumCoverage(string enumTypeId)
      => new RoslynEnumCoverageService((Internal.IRoslynLookup)this).GetEnumCoverage(enumTypeId);

  /// <summary>
  /// Generic static-initializer table extraction. Routes through
  /// <see cref="RoslynStaticTableExtractor"/> — keeps host wiring thin
  /// and isolates the IOperation walker in its own type so the
  /// name-leakage ratchet can scope cleanly. INV-EXTRACT-STATIC-TABLES-001.
  /// </summary>
  public StaticTableReport? GetStaticTables(string typeId, StaticTablesOptions options)
  {
    if (string.IsNullOrEmpty(typeId)) return null;
    var resolved = ResolveFromSource(typeId);
    if (resolved is not INamedTypeSymbol typeSymbol) return null;
    return RoslynStaticTableExtractor.Extract(_compilations, typeSymbol, typeId, options, BuildSymbolId);
  }

  public string[] FindImplementations(string symbolId)
  {
  // Prefer source-defined symbol for accurate type kind.
  var roslynSymbol = ResolveFromSource(symbolId);
  if (roslynSymbol == null) return Array.Empty<string>();

  // Compare via canonical Lifeblood symbol ID, NOT via ToDisplayString() or
  // SymbolEqualityComparer.Default. Cross-assembly symbols live in different
  // compilations. The source IGreeter (in module Core) and the metadata copy
  // of IGreeter seen from module Service are two distinct ISymbol instances
  // that SymbolEqualityComparer treats as non-equal. ToDisplayString() happens
  // to match them because display omits assembly qualification, but that's
  // fragile (the v0.6.0 BCL fix documented the drift class on nullability,
  // reduced names, and attribute round-trips).
  //
  // BuildSymbolId routes through CanonicalSymbolFormat, which produces the
  // SAME canonical ID string for source and metadata copies by design. This
  // is exactly the comparison strategy v0.6.0 Layer 3 adopted in FindReferences.
  // Reusing it here closes the same bug class in FindImplementations.
  var targetId = BuildSymbolId(roslynSymbol);
  var results = new List<string>();

  // Direct compilation scan. Reliable across cross-project boundaries
  // where AdhocWorkspace's SymbolFinder.FindImplementationsAsync may miss results.
  foreach (var compilation in _compilations.Values)
  {
  foreach (var type in EnumerateSourceTypes(compilation))
  {
  // Interface implementations
  if (roslynSymbol is INamedTypeSymbol targetInterface
  && targetInterface.TypeKind == TypeKind.Interface)
  {
  foreach (var iface in type.AllInterfaces)
  {
  if (BuildSymbolId(iface.OriginalDefinition) == targetId)
  {
  results.Add(Internal.SymbolIds.Type(RoslynSymbolExtractor.GetFullName(type)));
  break;
  }
  }
  }
  // Abstract/base class implementations
  else if (roslynSymbol is INamedTypeSymbol targetClass && targetClass.IsAbstract)
  {
  var baseType = type.BaseType;
  while (baseType != null)
  {
  if (BuildSymbolId(baseType.OriginalDefinition) == targetId)
  {
  results.Add(Internal.SymbolIds.Type(RoslynSymbolExtractor.GetFullName(type)));
  break;
  }
  baseType = baseType.BaseType;
  }
  }
  // Method overrides
  else if (roslynSymbol is IMethodSymbol)
  {
  foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
  {
  var overridden = member.OverriddenMethod;
  while (overridden != null)
  {
  if (BuildSymbolId(overridden) == targetId
  || BuildSymbolId(overridden.OriginalDefinition) == targetId)
  {
  results.Add(BuildSymbolId(member));
  break;
  }
  overridden = overridden.OverriddenMethod;
  }
  }
  }
  }
  }

  return results.Distinct().ToArray();
  }

  /// <summary>
  /// Enumerate all source-defined named types across all syntax trees in a compilation.
  /// </summary>
  private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(CSharpCompilation compilation)
  {
  foreach (var tree in compilation.SyntaxTrees)
  {
  var model = compilation.GetSemanticModel(tree);
  foreach (var node in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
  {
  if (model.GetDeclaredSymbol(node) is INamedTypeSymbol type)
  yield return type;
  }
  }
  }

  public SymbolAtPosition? GetSymbolAtPosition(string filePath, int line, int column)
  {
  // Find the syntax tree matching the file path
  foreach (var compilation in _compilations.Values)
  {
  foreach (var tree in compilation.SyntaxTrees)
  {
  if (tree.FilePath == null) continue;
  // Normalize both paths for comparison
  var treePath = tree.FilePath.Replace('\\', '/');
  var queryPath = filePath.Replace('\\', '/');
  if (!treePath.EndsWith(queryPath, StringComparison.OrdinalIgnoreCase)
  && !queryPath.EndsWith(treePath, StringComparison.OrdinalIgnoreCase)
  && !treePath.Equals(queryPath, StringComparison.OrdinalIgnoreCase))
  continue;

  var model = compilation.GetSemanticModel(tree);
  var text = tree.GetText();
  if (line < 1 || line > text.Lines.Count) continue;

  var position = text.Lines[line - 1].Start + Math.Max(0, column - 1);
  var token = tree.GetRoot().FindToken(position);
  var node = token.Parent;

  while (node != null)
  {
  var symbolInfo = model.GetSymbolInfo(node);
  if (symbolInfo.Symbol != null)
  {
  var sym = symbolInfo.Symbol;
  return new SymbolAtPosition
  {
  SymbolId = BuildSymbolId(sym),
  Name = sym.Name,
  Kind = sym.Kind.ToString(),
  QualifiedName = sym.ToDisplayString(),
  Documentation = GetXmlDocumentation(sym),
  };
  }

  var declared = model.GetDeclaredSymbol(node);
  if (declared != null)
  {
  return new SymbolAtPosition
  {
  SymbolId = BuildSymbolId(declared),
  Name = declared.Name,
  Kind = declared.Kind.ToString(),
  QualifiedName = declared.ToDisplayString(),
  Documentation = GetXmlDocumentation(declared),
  };
  }

  node = node.Parent;
  }
  return null;
  }
  }
  return null;
  }

  public string GetDocumentation(string symbolId)
  {
  // Prefer source-defined symbol. Metadata copies don't carry XML documentation.
  var roslynSymbol = ResolveFromSource(symbolId);
  if (roslynSymbol == null) return "";
  return GetXmlDocumentation(roslynSymbol);
  }

  private static string GetXmlDocumentation(ISymbol symbol)
      => Internal.XmlDocExtractor.ExtractSummary(symbol);

  /// <summary>
  /// Build the canonical Lifeblood symbol ID for a Roslyn symbol.
  /// Routes ALL parameter formatting through <see cref="Internal.CanonicalSymbolFormat"/>
  /// so the same C# symbol produces the same ID regardless of source/metadata origin.
  /// Indexers use the same <c>this[paramSig]</c> form as <see cref="RoslynSymbolExtractor"/>.
  /// </summary>
  internal static string BuildSymbolId(ISymbol symbol)
  {
  // Extension-method symbols arriving in reduced form (`x.Foo()` invocation
  // shape) carry a parameter list that drops the explicit `this` receiver.
  // The declaration path emits the unreduced form, so the consumer side must
  // normalize first or the canonical ids drift. Mirrors the same discipline
  // applied in RoslynEdgeExtractor.GetMethodId. INV-EXTRACT-EXTENSION-REDUCED-001.
  if (symbol is IMethodSymbol m && m.ReducedFrom != null) symbol = m.ReducedFrom;
  return symbol switch
  {
  INamedTypeSymbol type => Internal.SymbolIds.Type(RoslynSymbolExtractor.GetFullName(type)),
  IMethodSymbol method => Internal.SymbolIds.Method(
  RoslynSymbolExtractor.GetFullName(method.ContainingType),
  method.Name,
  Internal.CanonicalSymbolFormat.BuildParamSignature(method)),
  IFieldSymbol field => Internal.SymbolIds.Field(
  RoslynSymbolExtractor.GetFullName(field.ContainingType), field.Name),
  IPropertySymbol prop when prop.IsIndexer => Internal.SymbolIds.Property(
  RoslynSymbolExtractor.GetFullName(prop.ContainingType),
  $"this[{Internal.CanonicalSymbolFormat.BuildIndexerParamSignature(prop)}]"),
  IPropertySymbol prop => Internal.SymbolIds.Property(
  RoslynSymbolExtractor.GetFullName(prop.ContainingType), prop.Name),
  IEventSymbol evt => Internal.SymbolIds.Property(
  RoslynSymbolExtractor.GetFullName(evt.ContainingType), evt.Name),
  INamespaceSymbol ns => Internal.SymbolIds.Namespace(ns.ToDisplayString()),
  _ => $"unknown:{symbol.ToDisplayString()}",
  };
  }

  /// <summary>
  /// Single seam returning both the resolved module name AND its
  /// compilation. Callers that need the name (e.g. surfacing
  /// <c>resolvedModule</c> on a <see cref="CompileCheckResult"/> or
  /// asking <see cref="CollectDefines"/> for the right define set)
  /// no longer have to re-derive it from the picked compilation.
  /// Project-wide fallback: first entry of <c>_compilations</c>
  /// in insertion order. Returns <c>(null, null)</c> when
  /// <paramref name="moduleName"/> names a module that does not
  /// exist OR when there are no compilations at all.
  /// </summary>
  private (string? Module, CSharpCompilation? Compilation) ResolveCompilation(string? moduleName)
  {
  if (moduleName != null)
  return _compilations.TryGetValue(moduleName, out var c) ? (moduleName, c) : (null, null);
  var first = _compilations.FirstOrDefault();
  return first.Key == null ? (null, null) : (first.Key, first.Value);
  }

  private static DomainDiagnosticSeverity MapSeverity(Microsoft.CodeAnalysis.DiagnosticSeverity severity) =>
  severity switch
  {
  Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden => DomainDiagnosticSeverity.Hidden,
  Microsoft.CodeAnalysis.DiagnosticSeverity.Info => DomainDiagnosticSeverity.Info,
  Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => DomainDiagnosticSeverity.Warning,
  Microsoft.CodeAnalysis.DiagnosticSeverity.Error => DomainDiagnosticSeverity.Error,
  _ => DomainDiagnosticSeverity.Info,
  };

  public void Dispose()
  {
  if (_manager.IsValueCreated)
  _manager.Value.Dispose();
  }
}
