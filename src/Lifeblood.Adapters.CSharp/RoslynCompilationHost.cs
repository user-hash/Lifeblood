using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
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

    public RoslynCompilationHost(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _compilations = compilations;
        _manager = new Lazy<RoslynWorkspaceManager>(() => new RoslynWorkspaceManager(compilations));
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
    {
        var mgr = _manager.Value;
        if (mgr.Solution == null) return Array.Empty<DomainReferenceLocation>();

        var roslynSymbol = mgr.ResolveSymbol(symbolId, fallbackToStandalone: true);
        if (roslynSymbol == null) return Array.Empty<DomainReferenceLocation>();

        var references = SymbolFinder.FindReferencesAsync(roslynSymbol, mgr.Solution).GetAwaiter().GetResult();
        var results = new List<DomainReferenceLocation>();

        foreach (var refSymbol in references)
        {
            foreach (var location in refSymbol.Locations)
            {
                var lineSpan = location.Location.GetMappedLineSpan();
                var sourceText = location.Location.SourceTree?.GetText();
                var spanText = sourceText != null
                    ? sourceText.GetSubText(location.Location.SourceSpan).ToString()
                    : "";

                results.Add(new DomainReferenceLocation
                {
                    FilePath = lineSpan.Path ?? "",
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    SpanText = spanText,
                });
            }
        }

        return results.ToArray();
    }

    public DefinitionLocation? FindDefinition(string symbolId)
    {
        var mgr = _manager.Value;
        var roslynSymbol = mgr.ResolveSymbol(symbolId, fallbackToStandalone: true);
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
        var mgr = _manager.Value;
        var roslynSymbol = mgr.ResolveSymbol(symbolId, fallbackToStandalone: true);
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
        var mgr = _manager.Value;
        var roslynSymbol = mgr.ResolveSymbol(symbolId, fallbackToStandalone: true);
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

    private static string BuildSymbolId(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol type => Internal.SymbolIds.Type(RoslynSymbolExtractor.GetFullName(type)),
            IMethodSymbol method => Internal.SymbolIds.Method(
                RoslynSymbolExtractor.GetFullName(method.ContainingType),
                method.Name,
                string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))),
            IFieldSymbol field => Internal.SymbolIds.Field(
                RoslynSymbolExtractor.GetFullName(field.ContainingType), field.Name),
            IPropertySymbol prop => Internal.SymbolIds.Property(
                RoslynSymbolExtractor.GetFullName(prop.ContainingType), prop.Name),
            IEventSymbol evt => Internal.SymbolIds.Property(
                RoslynSymbolExtractor.GetFullName(evt.ContainingType), evt.Name),
            INamespaceSymbol ns => Internal.SymbolIds.Namespace(ns.ToDisplayString()),
            _ => $"unknown:{symbol.ToDisplayString()}",
        };
    }

    private static bool IsFromSource(ISymbol? symbol)
    {
        if (symbol == null) return false;
        return symbol.Locations.Any(l => l.IsInSource);
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
