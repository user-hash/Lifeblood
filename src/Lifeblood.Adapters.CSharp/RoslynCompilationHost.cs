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
public sealed class RoslynCompilationHost : ICompilationHost, IDisposable
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

    public DiagnosticInfo[] GetDiagnostics(string? moduleName = null)
    {
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

    public CompileCheckResult CompileCheck(string code, string? moduleName = null)
    {
        var targetCompilation = ResolveCompilation(moduleName);
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

        // Collect pre-existing diagnostic IDs so we only report NEW diagnostics from the snippet
        var preExistingIds = new HashSet<string>(
            targetCompilation.GetDiagnostics()
                .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
                .Select(d => $"{d.Id}:{d.Location.GetMappedLineSpan().Path}:{d.Location.GetMappedLineSpan().StartLinePosition.Line}"));

        var tree = CSharpSyntaxTree.ParseText(code);
        var testCompilation = targetCompilation.AddSyntaxTrees(tree);

        using var ms = new MemoryStream();
        var emitResult = testCompilation.Emit(ms);

        // Filter to only diagnostics introduced by the snippet (not pre-existing in the compilation)
        var snippetDiagnostics = emitResult.Diagnostics
            .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .Where(d =>
            {
                var key = $"{d.Id}:{d.Location.GetMappedLineSpan().Path}:{d.Location.GetMappedLineSpan().StartLinePosition.Line}";
                return !preExistingIds.Contains(key);
            })
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
                };
            })
            .ToArray();

        // Success = no NEW errors from the snippet (pre-existing errors don't count)
        var hasNewErrors = snippetDiagnostics.Any(d => d.Severity == DomainDiagnosticSeverity.Error);

        return new CompileCheckResult
        {
            Success = !hasNewErrors,
            Diagnostics = snippetDiagnostics,
        };
    }

    public DomainReferenceLocation[] FindReferences(string symbolId)
        => FindReferences(symbolId, FindReferencesOptions.Default);

    public DomainReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options)
    {
        // Direct compilation scan — reliable across cross-project boundaries
        // where AdhocWorkspace's SymbolFinder.FindReferencesAsync may miss results.
        //
        // Match by CANONICAL Lifeblood symbol ID (BuildSymbolId), NOT by Roslyn's
        // ToDisplayString. The display string can diverge subtly across the source/metadata
        // boundary (nullability, reduced names, attribute round-trips), causing legitimate
        // call sites to be silently dropped. The canonical builder is namespace-walking +
        // explicit param types — it produces the same string for source and metadata symbols
        // because both feed through identical RoslynSymbolExtractor.GetFullName + ToDisplayString.
        //
        // First we resolve the requested symbol once to get its OWN canonical ID. Then for
        // every visited node we compute the canonical ID and compare strings. This is the
        // same builder the graph uses, so any ID format the graph emits is also matched here.
        var resolvedTarget = ResolveFromSource(symbolId);
        if (resolvedTarget == null) return Array.Empty<DomainReferenceLocation>();
        var targetCanonicalId = BuildSymbolId(resolvedTarget);

        var results = new List<DomainReferenceLocation>();
        var seen = new HashSet<(string, int, int)>(); // (filePath, line, column) dedup

        foreach (var compilation in _compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var sourceText = tree.GetText();

                foreach (var node in root.DescendantNodes())
                {
                    var symbolInfo = model.GetSymbolInfo(node);
                    var resolved = symbolInfo.Symbol;
                    if (resolved == null) continue;

                    // Compare canonical IDs. Walk to OriginalDefinition for constructed
                    // generics so List<int>.Add and List<string>.Add both match List<T>.Add.
                    var resolvedId = BuildSymbolId(resolved);
                    if (resolvedId != targetCanonicalId)
                    {
                        var originalId = BuildSymbolId(resolved.OriginalDefinition);
                        if (originalId != targetCanonicalId) continue;
                    }

                    var span = node.GetLocation().GetMappedLineSpan();
                    var line = span.StartLinePosition.Line + 1;
                    var column = span.StartLinePosition.Character + 1;
                    var filePath = span.Path ?? "";

                    if (!seen.Add((filePath, line, column))) continue;

                    var spanText = sourceText.GetSubText(node.Span).ToString();
                    results.Add(new DomainReferenceLocation
                    {
                        FilePath = filePath,
                        Line = line,
                        Column = column,
                        SpanText = spanText,
                    });
                }
            }
        }

        // LB-FR-003 / Plan v4 §2.6 / Correction 2: declaration locations are
        // an opt-in operation policy on the host, NOT a side-effect of resolver
        // merging. Roslyn's ISymbol.Locations returns one entry per partial
        // declaration for partial types — exactly the data the user wants
        // surfaced when querying "where is this type defined?"
        if (options.IncludeDeclarations)
        {
            var declSeen = new HashSet<(string, int, int)>(seen);
            if (resolvedTarget != null)
            {
                foreach (var location in resolvedTarget.Locations.Where(l => l.IsInSource))
                {
                    var span = location.GetMappedLineSpan();
                    var line = span.StartLinePosition.Line + 1;
                    var column = span.StartLinePosition.Character + 1;
                    var filePath = span.Path ?? "";
                    if (!declSeen.Add((filePath, line, column))) continue;

                    results.Add(new DomainReferenceLocation
                    {
                        FilePath = filePath,
                        Line = line,
                        Column = column,
                        SpanText = "(declaration)",
                    });
                }
            }
        }

        return results.ToArray();
    }

    /// <summary>
    /// Resolve a symbol ID to its Roslyn display string for cross-compilation matching.
    /// Uses ResolveFromSource to prefer the source-defined symbol, whose display string
    /// is canonical (correct nullability, no assembly qualification).
    /// </summary>
    private string? ResolveDisplayString(string symbolId)
        => ResolveFromSource(symbolId)?.ToDisplayString();

    /// <summary>
    /// Resolve a symbol ID, preferring the source-defined copy over metadata copies.
    /// In a multi-assembly workspace, the same symbol exists as source in its home module
    /// and as metadata in every consumer. The source copy has the canonical display string,
    /// source locations, and XML documentation — metadata copies have none of these.
    /// </summary>
    private ISymbol? ResolveFromSource(string symbolId)
    {
        var mgr = _manager.Value;
        var roslynSymbol = mgr.ResolveSymbol(symbolId, fallbackToStandalone: true);
        if (roslynSymbol != null && IsFromSource(roslynSymbol)) return roslynSymbol;

        // Workspace returned metadata copy — search standalone compilations for source
        var parsed = RoslynWorkspaceManager.ParseSymbolId(symbolId);
        if (parsed.Kind == null || parsed.Parts == null) return roslynSymbol;

        foreach (var compilation in _compilations.Values)
        {
            var found = RoslynWorkspaceManager.FindInCompilation(compilation, parsed);
            if (found != null && IsFromSource(found)) return found;
        }
        return roslynSymbol; // metadata fallback — better than null
    }

    /// <summary>
    /// Returns true if the symbol has at least one source location (not metadata-only).
    /// </summary>
    private static bool IsFromSource(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    public DefinitionLocation? FindDefinition(string symbolId)
    {
        // Resolve with source preference — a cross-assembly symbol may resolve to its
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

    public string[] FindImplementations(string symbolId)
    {
        // Prefer source-defined symbol for accurate type kind and display string comparison.
        var roslynSymbol = ResolveFromSource(symbolId);
        if (roslynSymbol == null) return Array.Empty<string>();

        var results = new List<string>();

        // Direct compilation scan — reliable across cross-project boundaries
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
                        if (iface.OriginalDefinition.ToDisplayString() == targetInterface.ToDisplayString())
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
                        if (baseType.OriginalDefinition.ToDisplayString() == targetClass.ToDisplayString())
                        {
                            results.Add(Internal.SymbolIds.Type(RoslynSymbolExtractor.GetFullName(type)));
                            break;
                        }
                        baseType = baseType.BaseType;
                    }
                }
                // Method overrides
                else if (roslynSymbol is IMethodSymbol targetMethod)
                {
                    foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        var overridden = member.OverriddenMethod;
                        while (overridden != null)
                        {
                            if (overridden.ToDisplayString() == targetMethod.ToDisplayString())
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
        // Prefer source-defined symbol — metadata copies don't carry XML documentation.
        var roslynSymbol = ResolveFromSource(symbolId);
        if (roslynSymbol == null) return "";
        return GetXmlDocumentation(roslynSymbol);
    }

    private static string GetXmlDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return "";
        // Extract the <summary> content for a clean presentation
        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var inner = xml.Substring(start + 9, end - start - 9).Trim();
            // Strip XML whitespace/newlines
            return string.Join(" ", inner.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()));
        }
        return xml;
    }

    /// <summary>
    /// Build the canonical Lifeblood symbol ID for a Roslyn symbol.
    /// Routes ALL parameter formatting through <see cref="Internal.CanonicalSymbolFormat"/>
    /// so the same C# symbol produces the same ID regardless of source/metadata origin.
    /// Indexers use the same <c>this[paramSig]</c> form as <see cref="RoslynSymbolExtractor"/>.
    /// </summary>
    private static string BuildSymbolId(ISymbol symbol)
    {
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

    private CSharpCompilation? ResolveCompilation(string? moduleName)
    {
        if (moduleName != null)
            return _compilations.TryGetValue(moduleName, out var c) ? c : null;
        return _compilations.Values.FirstOrDefault();
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
