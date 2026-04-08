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
            ? new[] { single }
            : _compilations.Values.ToArray();

        foreach (var compilation in compilations)
        {
            foreach (var diag in compilation.GetDiagnostics())
            {
                if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden) continue;

                var lineSpan = diag.Location.GetMappedLineSpan();
                results.Add(new DiagnosticInfo
                {
                    Id = diag.Id,
                    Message = diag.GetMessage(),
                    Severity = MapSeverity(diag.Severity),
                    FilePath = lineSpan.Path ?? "",
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
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

        var tree = CSharpSyntaxTree.ParseText(code);
        var testCompilation = targetCompilation.AddSyntaxTrees(tree);

        using var ms = new MemoryStream();
        var emitResult = testCompilation.Emit(ms);

        var diagnostics = emitResult.Diagnostics
            .Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
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

        return new CompileCheckResult
        {
            Success = emitResult.Success,
            Diagnostics = diagnostics,
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

    private ISymbol? ResolveSymbol(string symbolId)
    {
        // Parse symbol ID: "type:Namespace.ClassName" or "method:Namespace.ClassName.MethodName(...)"
        var prefix = symbolId.IndexOf(':');
        if (prefix < 0) return null;

        var kind = symbolId.Substring(0, prefix);
        var qualifiedName = symbolId.Substring(prefix + 1);

        // Strip parameter signature for methods
        var parenIdx = qualifiedName.IndexOf('(');
        var nameOnly = parenIdx >= 0 ? qualifiedName.Substring(0, parenIdx) : qualifiedName;
        var parts = nameOnly.Split('.');

        foreach (var compilation in _compilations.Values)
        {
            INamespaceOrTypeSymbol current = compilation.GlobalNamespace;

            foreach (var part in parts)
            {
                var member = current.GetMembers(part).FirstOrDefault();
                if (member is INamespaceOrTypeSymbol ns)
                    current = ns;
                else if (member != null)
                    return member; // Found a method/field/property
                else
                    break;
            }

            if (current != compilation.GlobalNamespace)
            {
                if (kind == "type" && current is INamedTypeSymbol) return current;
                if (kind == "method") return current.GetMembers().OfType<IMethodSymbol>().FirstOrDefault();
                if (kind == "field") return current.GetMembers().OfType<IFieldSymbol>().FirstOrDefault();
            }
        }

        return null;
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
