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
public sealed class RoslynCompilationHost : ICompilationHost
{
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private AdhocWorkspace? _workspace;
    private Solution? _solution;

    public RoslynCompilationHost(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _compilations = compilations;
    }

    public bool IsAvailable => _compilations.Count > 0;

    public DiagnosticInfo[] GetDiagnostics(string? moduleName = null)
    {
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
        EnsureWorkspace();
        if (_solution == null) return Array.Empty<DomainReferenceLocation>();

        var roslynSymbol = ResolveSymbol(symbolId);
        if (roslynSymbol == null) return Array.Empty<DomainReferenceLocation>();

        var references = SymbolFinder.FindReferencesAsync(roslynSymbol, _solution).GetAwaiter().GetResult();
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

    private CSharpCompilation? ResolveCompilation(string? moduleName)
    {
        if (moduleName != null)
            return _compilations.TryGetValue(moduleName, out var c) ? c : null;
        return _compilations.Values.FirstOrDefault();
    }

    /// <summary>
    /// Resolve a symbol from the workspace solution's project compilations.
    /// Must use workspace-owned compilations — standalone _compilations produce symbols
    /// that Renamer/SymbolFinder cannot match against the Solution.
    /// </summary>
    private ISymbol? ResolveSymbol(string symbolId)
    {
        var (kind, parts) = ParseSymbolId(symbolId);
        if (kind == null || parts == null) return null;

        // Resolve from workspace projects so the symbol belongs to the Solution
        if (_solution != null)
        {
            foreach (var project in _solution.Projects)
            {
                var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
                if (compilation == null) continue;

                var found = FindInCompilation(compilation, kind, parts);
                if (found != null) return found;
            }
        }

        // Fallback to standalone compilations (for non-workspace operations)
        foreach (var compilation in _compilations.Values)
        {
            var found = FindInCompilation(compilation, kind, parts);
            if (found != null) return found;
        }

        return null;
    }

    private static ISymbol? FindInCompilation(Compilation compilation, string kind, string[] parts)
    {
        INamespaceOrTypeSymbol current = compilation.GlobalNamespace;

        foreach (var part in parts)
        {
            var member = current.GetMembers(part).FirstOrDefault();
            if (member is INamespaceOrTypeSymbol ns)
                current = ns;
            else if (member != null)
                return member;
            else
                break;
        }

        if (!SymbolEqualityComparer.Default.Equals(current, compilation.GlobalNamespace))
        {
            if (kind == "type" && current is INamedTypeSymbol) return current;
            if (kind == "method") return current.GetMembers().OfType<IMethodSymbol>().FirstOrDefault();
            if (kind == "field") return current.GetMembers().OfType<IFieldSymbol>().FirstOrDefault();
        }

        return null;
    }

    private static (string? kind, string[]? parts) ParseSymbolId(string symbolId)
    {
        var prefix = symbolId.IndexOf(':');
        if (prefix < 0) return (null, null);

        var kind = symbolId.Substring(0, prefix);
        var qualifiedName = symbolId.Substring(prefix + 1);

        var parenIdx = qualifiedName.IndexOf('(');
        var nameOnly = parenIdx >= 0 ? qualifiedName.Substring(0, parenIdx) : qualifiedName;
        return (kind, nameOnly.Split('.'));
    }

    private void EnsureWorkspace()
    {
        if (_workspace != null) return;

        _workspace = new AdhocWorkspace();
        var solutionInfo = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default);
        _workspace.AddSolution(solutionInfo);

        foreach (var (name, compilation) in _compilations)
        {
            var projectId = ProjectId.CreateNewId(name);
            var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
                metadataReferences: compilation.References);
            _workspace.AddProject(projectInfo);

            foreach (var tree in compilation.SyntaxTrees)
            {
                var docId = DocumentId.CreateNewId(projectId);
                var docInfo = DocumentInfo.Create(docId,
                    Path.GetFileName(tree.FilePath ?? "unknown.cs"),
                    sourceCodeKind: SourceCodeKind.Regular,
                    loader: TextLoader.From(TextAndVersion.Create(tree.GetText(), VersionStamp.Default)),
                    filePath: tree.FilePath);
                _workspace.AddDocument(docInfo);
            }
        }

        _solution = _workspace.CurrentSolution;
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
}
