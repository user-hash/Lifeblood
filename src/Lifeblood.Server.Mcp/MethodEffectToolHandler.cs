using System.Text.Json;
using Lifeblood.Adapters.Performance;
using Lifeblood.Analysis;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>Composes one leased graph candidate plan with one bounded operation scan.</summary>
internal sealed class MethodEffectToolHandler(GraphSession session)
{
    public MethodEffectReport Execute(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var bound = ToolRequestBinder.BindMethodEffects(arguments);
        var request = new MethodEffectRequest
        {
            RootSymbolIds = bound.RootSymbolIds,
            ProfileScope = string.IsNullOrWhiteSpace(bound.ProfileScope) ? session.RetainedProfileName : bound.ProfileScope.Trim(),
            MaxDepth = bound.MaxDepth,
            MaxMethods = bound.MaxMethods,
            MaxFacts = bound.MaxFacts,
            MaxEvidencePerMethod = bound.MaxEvidencePerMethod,
        };
        var graph = session.Graph ?? throw new InvalidOperationException("Method effects require a loaded semantic graph.");
        var provider = session.OperationFactProvider ?? throw new InvalidOperationException("Method effects require the selected publication's operation-fact provider.");
        var plan = MethodEffectAnalyzer.Plan(graph, request);
        var capture = bound.RuntimeEvidencePath == null ? null
            : new ImportPerformanceEvidenceUseCase(new RuntimePerformanceEvidenceImporter()).Execute(
                RuntimeEvidenceFileReader.Read(session.FileSystem, session.ProjectRoot, bound.RuntimeEvidencePath, null, 10_000));
        if (capture != null)
            foreach (var document in capture.NativeArtifacts.SelectMany(artifact => artifact.Documents))
                ValidateDocumentPath(document.WorkspaceFilePath);
        var owners = plan.Matches.Select(match => match.ContainingSymbolId).Distinct(StringComparer.Ordinal).ToArray();
        var files = owners.Select(owner => graph.GetSymbol(owner)?.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
            .Distinct(StringComparer.Ordinal).ToArray();
        var facts = new List<OperationFact>();
        var receipt = new ScanOperationFactsUseCase(provider).Execute(new OperationFactQuery
        {
            ProfileScope = request.ProfileScope,
            FilePaths = files,
            ContainingSymbolIds = owners,
            IncludeImplicit = true,
            IncludeCallableDeclarations = true,
            MaxFacts = request.MaxFacts,
        }, fact => { facts.Add(fact); return true; }, cancellationToken);
        var report = MethodEffectAnalyzer.Analyze(graph, request, plan, facts, receipt, cancellationToken);
        if (capture == null) return report;
        var observations = facts.Where(fact => fact.CallableDeclaration != null)
            .SelectMany(fact => fact.CallableDeclaration!.SourceChecksums.Select(checksum => new NativeSourceObservation(
                RelativePath(fact.Source.FilePath), fact.ModuleName, fact.ProfileScope, checksum.Key, checksum.Value)))
            .Distinct().ToArray();
        return new MethodEffectReport
        {
            ScanReceipt = report.ScanReceipt,
            CandidateScopes = report.CandidateScopes,
            Methods = report.Methods,
            Limitations = report.Limitations,
            NativeSourceEvidence = NativeSourceJoinAnalyzer.Compare(capture, observations, receipt, OperatingSystem.IsWindows(), cancellationToken),
        };
    }

    private void ValidateDocumentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or "..")
            || !WorkspacePathIdentity.Contains(session.ProjectRoot, WorkspacePathIdentity.ResolveFromWorkspace(session.ProjectRoot, path)))
            throw new ArgumentException("Native workspaceFilePath must be a normalized workspace-relative POSIX path without traversal.");
    }

    private string RelativePath(string path)
    {
        var full = WorkspacePathIdentity.ResolveFromWorkspace(session.ProjectRoot, path);
        return WorkspacePathIdentity.Contains(session.ProjectRoot, full)
            ? Path.GetRelativePath(session.ProjectRoot, full).Replace('\\', '/') : "";
    }
}
