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
