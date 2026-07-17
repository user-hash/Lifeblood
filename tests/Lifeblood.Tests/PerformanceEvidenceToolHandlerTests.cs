using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class PerformanceEvidenceToolHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lifeblood-performance-{Guid.NewGuid():N}");
    private readonly PhysicalFileSystem _fs = new();

    public PerformanceEvidenceToolHandlerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Perf.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Host.cs"), """
            namespace Perf;
            public static class Host
            {
                public const string Marker = "HotMarker";
                public static void Tick() { }
            }
            """);
        WriteCapture("baseline.json", "dense", "S23", 2.0);
        WriteCapture("candidate.json", "dense", "S8", 3.0);
        WriteCapture("unlike.json", "other", "S8", 3.0);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Execute_Correlate_UsesLivePublicationWithoutAdditionalSemanticBase()
    {
        using var session = LoadSession();
        var markerField = session.Graph!.Symbols.Single(symbol => symbol.Kind == SymbolKind.Field && symbol.Name == "Marker");
        var handler = new PerformanceEvidenceToolHandler(session, new LifebloodInvariantProvider(_fs));

        var result = handler.Execute(JsonSerializer.SerializeToElement(new
        {
            action = "correlate",
            sourcePath = "baseline.json",
            markerAliases = new[] { new { marker = "HotMarker", symbolId = markerField.Id } },
        }));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonOptions));
        var root = json.RootElement;

        Assert.Equal("correlate", root.GetProperty("action").GetString());
        Assert.Equal(0, root.GetProperty("additionalSemanticBaseCount").GetInt32());
        var correlation = root.GetProperty("semanticCorrelation");
        Assert.Equal(1, correlation.GetProperty("uniqueCount").GetInt32());
        var marker = correlation.GetProperty("markers")[0];
        Assert.Equal("CallerAlias", marker.GetProperty("resolutionSource").GetString());
        Assert.Equal(markerField.Id, marker.GetProperty("candidates")[0].GetProperty("symbolId").GetString());
        Assert.Equal("RetainedSyntaxTrees", correlation.GetProperty("sourceEvidence").GetProperty("executionMode").GetString());
        Assert.Equal(0, correlation.GetProperty("sourceEvidence").GetProperty("additionalSemanticBaseCount").GetInt32());
    }

    [Fact]
    public void Execute_Compare_EmitsDeltasForComparableCaptureAndWithholdsThemForReject()
    {
        using var session = LoadSession();
        var handler = new PerformanceEvidenceToolHandler(session, new LifebloodInvariantProvider(_fs));

        var comparable = handler.Execute(JsonSerializer.SerializeToElement(new
        {
            action = "compare",
            sourcePath = "baseline.json",
            candidatePath = "candidate.json",
        }));
        using var comparableJson = JsonDocument.Parse(JsonSerializer.Serialize(comparable, JsonOptions));
        Assert.Equal("Comparable", comparableJson.RootElement.GetProperty("comparison").GetProperty("verdict").GetString());
        Assert.NotEqual(0, comparableJson.RootElement.GetProperty("comparison").GetProperty("returnedDeltaCount").GetInt32());

        var rejected = handler.Execute(JsonSerializer.SerializeToElement(new
        {
            action = "compare",
            sourcePath = "baseline.json",
            candidatePath = "unlike.json",
        }));
        using var rejectedJson = JsonDocument.Parse(JsonSerializer.Serialize(rejected, JsonOptions));
        Assert.Equal("RejectComparison", rejectedJson.RootElement.GetProperty("comparison").GetProperty("verdict").GetString());
        Assert.Equal(0, rejectedJson.RootElement.GetProperty("comparison").GetProperty("deltas").GetArrayLength());
    }

    private GraphSession LoadSession()
    {
        var session = new GraphSession(_fs);
        session.Load(_root, graphPath: null, rulesPath: null, readOnly: false);
        return session;
    }

    private void WriteCapture(string name, string scenario, string device, double value)
    {
        var capture = new
        {
            schema = "lifeblood.performance-capture@1",
            captureId = name,
            identity = new
            {
                scenarioId = scenario,
                productName = "DAWG",
                appVersion = "1.2.3",
                buildId = "build-1",
                gitCommit = "abc1234",
                dirty = false,
                defineProfiles = new[] { "Editor", "Player" },
                featureFlags = new[] { "Burst" },
            },
            device = new
            {
                deviceModel = device,
                deviceClass = "phone",
                platform = "Android",
                operatingSystem = "Android 15",
                processor = device + " CPU",
                graphicsDevice = device + " GPU",
                graphicsApi = "Vulkan",
                width = 1920,
                height = 1080,
                refreshRateHz = 60,
            },
            workload = new
            {
                fingerprint = "work-1",
                captureMode = "ProfilerRecorder",
                audioSampleRate = 48000,
                audioBufferFrames = 256,
                targetFrameRate = 60,
                counters = new { voices = 8 },
            },
            measurements = new[]
            {
                new { stage = "play", category = "CPU", marker = "HotMarker", statistic = "Average", unit = "ms", value },
            },
        };
        File.WriteAllText(Path.Combine(_root, name), JsonSerializer.Serialize(capture));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
