using System.Diagnostics;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Output;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Rules;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.CLI;

internal static class AnalyzeJsonCommand
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static int Run(string[] args, TextWriter output, IFileSystem? fileSystem = null, IProgressSink? progress = null)
    {
        var startedAtUtc = DateTime.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var fs = fileSystem ?? new PhysicalFileSystem();
        var identity = CliIdentity.Capture();
        try
        {
            var options = Parse(args);
            var root = FileScopeObservation.NormalizeRoot(options["--project"]);
            if (!fs.DirectoryExists(root)) throw new ArgumentException("The project directory does not exist.");
            var config = new AnalysisConfig { RetainCompilations = false,
                DefineProfiles = options.TryGetValue("--profiles", out var profiles) ? profiles.Split(',') : null };
            if (config.DefineProfiles?.Any(string.IsNullOrWhiteSpace) == true)
                throw new ArgumentException("--profiles requires nonempty comma-separated profile names.");
            var adapter = new RoslynWorkspaceAnalyzer(fs, new UnityDefineProfileResolver(fs));
            var rulesSource = options.GetValueOrDefault("--rules");
            var (rules, rulesBefore) = ResolveRules(rulesSource, root, fs);
            var before = adapter.CaptureAnalysisInputs(root, config);
            var workspace = WorkspaceKey.FromCanonicalIdentity(OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root);
            WorkspaceAnalysisIdentity Identity(SourceFingerprint source, IEnumerable<string> activeProfiles, RuleSetIdentity ruleIdentity)
                => new(workspace, new AnalysisSpec(activeProfiles, config.ExcludePathGlobs, AnalysisRetentionMode.GraphOnly,
                    AnalysisDescriptorPolicy.WorkspaceDiscovery, ruleIdentity, adapter.DeclarationIdentityPolicy), source);
            var beforeIdentity = Identity(before.SourceFingerprint, before.EffectiveDefineProfiles, rulesBefore);
            var result = new AnalyzeWorkspaceUseCase(adapter, progress, new ProcessUsageProbe()).Execute(root, config);
            var analyzedIdentity = Identity(adapter.CurrentSourceFingerprint!, adapter.RetainedProfileNames, rulesBefore);
            var rulesResult = AnalysisPipeline.Run(result.Graph, rules);
            var after = adapter.CaptureAnalysisInputs(root, config);
            var (_, rulesAfter) = ResolveRules(rulesSource, root, fs);
            var afterIdentity = Identity(after.SourceFingerprint, after.EffectiveDefineProfiles, rulesAfter);
            var stable = beforeIdentity.HasSameSemanticInputs(analyzedIdentity) && analyzedIdentity.HasSameSemanticInputs(afterIdentity);
            var success = stable && rulesResult.Violations.Length == 0;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, executionMode = "standaloneBatch", success,
                failure = !stable ? "analysis-input-changed" : rulesResult.Violations.Length > 0 ? "rule-violations" : null,
                publication = "notPublished", daemonContacted = false, semanticServicesRetained = false,
                startedAtUtc, finishedAtUtc = DateTime.UtcNow, callerWallTimeMs = elapsed.ElapsedMilliseconds,
                execution = identity, projectRoot = root,
                inputVerification = new { status = stable ? "matchedAtObservations" : "driftDetected",
                    before = WorkspaceAnalysisDescriptor.From(beforeIdentity),
                    analyzed = WorkspaceAnalysisDescriptor.From(analyzedIdentity),
                    after = WorkspaceAnalysisDescriptor.From(afterIdentity) },
                summary = new { symbols = result.Graph.Symbols.Count, edges = result.Graph.Edges.Count,
                    modules = rulesResult.Metrics.TotalModules, types = rulesResult.Metrics.TotalTypes,
                    files = rulesResult.Metrics.TotalFiles },
                rules = new { sourceKind = rulesBefore.SourceKind.ToString(), rulesBefore.Source,
                    fingerprint = rulesBefore.Fingerprint.Value, violations = rulesResult.Violations },
                diagnostics = new { graphValidation = "passed", compilerDiagnostics = "notCollected" },
                skipped = adapter.SkippedFiles,
                usage = result.Usage,
                limitations = new[] { "Input equality is observed before, from analysis and afterward; it is not an external-editor lock or an atomic filesystem snapshot. Streaming graph analysis does not certify a build, Unity/Pipeline options, native interop or runtime behavior. Usage covers graph extraction/validation; callerWallTimeMs also includes identity observations and rule analysis." },
            }, Json));
            return !stable ? 3 : success ? 0 : 2;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, executionMode = "standaloneBatch", success = false,
                failure = "analysis-unavailable", message = ex.Message, execution = identity,
                startedAtUtc, finishedAtUtc = DateTime.UtcNow, callerWallTimeMs = elapsed.ElapsedMilliseconds,
                publication = "notPublished", daemonContacted = false }, Json));
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] is not ("--project" or "--profiles" or "--rules") || i + 1 == args.Length
                || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1])
                || !options.TryAdd(args[i], args[++i]))
                throw new ArgumentException("Usage: lifeblood analyze --project <root> --json [--profiles Editor,Player] [--rules <pack-or-file>]. Graph imports and incremental mode use their existing commands.");
        }
        if (!json || !options.ContainsKey("--project")) throw new ArgumentException("--json requires an explicit --project root.");
        return options;
    }

    private static (ArchitectureRule[]? Rules, RuleSetIdentity Identity) ResolveRules(string? source, string root, IFileSystem fs)
    {
        if (source == null) return (null, RulePacks.Identify(null, RuleSetSourceKind.None, null));
        if (RulePacks.ResolveBuiltIn(source) is { } builtIn)
            return (builtIn, RulePacks.Identify(builtIn, RuleSetSourceKind.BuiltIn, source));
        var path = Path.GetFullPath(source, root);
        var rules = RulePacks.ParseJson(fs.ReadAllText(path)) ?? throw new InvalidDataException("Rules file has no rules array.");
        return (rules, RulePacks.Identify(rules, RuleSetSourceKind.File, path));
    }
}
