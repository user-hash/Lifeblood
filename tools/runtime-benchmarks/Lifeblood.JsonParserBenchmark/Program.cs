using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Lifeblood.Server.Mcp;

var config = BenchmarkConfig.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.OutputPath))!);

var payload = """
{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"lifeblood_analyze","arguments":{"projectPath":"/path/to/workspace","incremental":true,"allowFullFallback":true,"defineProfiles":["Editor","Player"]}}}
""";
var payloadBytes = Encoding.UTF8.GetBytes(payload);
var baselineLegacy = CurrentString(payload, strictJson: false);
var baselineStrict = CurrentString(payload, strictJson: true);

var variants = new[]
{
    Measure("string-current-legacy", "current-string", strictJson: false, config, () => CurrentString(payload, strictJson: false), baselineLegacy),
    Measure("utf8-span-legacy", "utf8-span", strictJson: false, config, () => Utf8Span(payloadBytes, strictJson: false), baselineLegacy),
    Measure("pipe-reader-buffered-legacy", "pipe-reader-buffered", strictJson: false, config, () => PipeReaderBuffered(payloadBytes, strictJson: false), baselineLegacy),
    Measure("string-current-strict", "current-string", strictJson: true, config, () => CurrentString(payload, strictJson: true), baselineStrict),
    Measure("utf8-span-strict", "utf8-span", strictJson: true, config, () => Utf8Span(payloadBytes, strictJson: true), baselineStrict),
    Measure("pipe-reader-buffered-strict", "pipe-reader-buffered", strictJson: true, config, () => PipeReaderBuffered(payloadBytes, strictJson: true), baselineStrict),
};

var report = new
{
    schemaVersion = 1,
    benchmarkRunId = config.BenchmarkRunId,
    generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
    iterations = config.Iterations,
    warmupIterations = config.WarmupIterations,
    payload = new
    {
        chars = payload.Length,
        utf8Bytes = payloadBytes.Length,
    },
    adoptionPosture = "measurement-only; production MCP stdio still uses line/string parsing",
    notes = new[]
    {
        "utf8-span measures the lower-level parser shape that PipeReader would feed after buffering a complete JSON-RPC line.",
        "pipe-reader-buffered measures PipeReader read overhead plus the same utf8 parser, not a production transport rewrite.",
        "Every variant consumes the production source-generated request parser and envelope policy.",
    },
    variants,
};

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(config.OutputPath, JsonSerializer.Serialize(report, jsonOptions), Encoding.UTF8);
Console.WriteLine($"Wrote JSON parser benchmark report to {config.OutputPath}");

static JsonRpcRequest? CurrentString(string payload, bool strictJson)
    => McpJsonRequestParser.DeserializeRequest(payload, JsonOptions.Default, strictJson);

static JsonRpcRequest? Utf8Span(byte[] payloadBytes, bool strictJson)
    => McpJsonRequestParser.DeserializeRequest(payloadBytes, strictJson);

static JsonRpcRequest? PipeReaderBuffered(byte[] payloadBytes, bool strictJson)
{
    using var stream = new MemoryStream(payloadBytes, writable: false);
    var reader = PipeReader.Create(stream);
    var read = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
    try
    {
        return DeserializeSequence(read.Buffer, strictJson);
    }
    finally
    {
        reader.AdvanceTo(read.Buffer.End);
        reader.Complete();
    }
}

static JsonRpcRequest? DeserializeSequence(ReadOnlySequence<byte> sequence, bool strictJson)
{
    if (sequence.IsSingleSegment)
    {
        return McpJsonRequestParser.DeserializeRequest(sequence.FirstSpan, strictJson);
    }

    var bytes = sequence.ToArray();
    return Utf8Span(bytes, strictJson);
}

static VariantResult Measure(
    string name,
    string parser,
    bool strictJson,
    BenchmarkConfig config,
    Func<JsonRpcRequest?> action,
    JsonRpcRequest? baseline)
{
    for (var i = 0; i < config.WarmupIterations; i++)
    {
        _ = action();
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var sw = Stopwatch.StartNew();
    var success = 0;
    JsonRpcRequest? last = null;
    for (var i = 0; i < config.Iterations; i++)
    {
        last = action();
        if (last is not null)
        {
            success++;
        }
    }

    sw.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
    return new VariantResult(
        name,
        parser,
        strictJson,
        sw.Elapsed.TotalMilliseconds,
        cpuMs,
        sw.Elapsed.TotalMilliseconds * 1000.0 / config.Iterations,
        allocated,
        allocated / (double)config.Iterations,
        success,
        Equivalent(baseline, last),
        parser == "pipe-reader-buffered"
            ? "PipeReader candidate is deliberately buffered to preserve current one-line JSON-RPC framing."
            : "");
}

static bool Equivalent(JsonRpcRequest? expected, JsonRpcRequest? actual)
{
    if (expected is null || actual is null)
    {
        return expected is null && actual is null;
    }

    return expected.JsonRpc == actual.JsonRpc
        && expected.Method == actual.Method
        && expected.Id?.ToString() == actual.Id?.ToString()
        && expected.Params?.ToString() == actual.Params?.ToString();
}

internal sealed record VariantResult(
    string Name,
    string Parser,
    bool StrictJson,
    double ElapsedMs,
    double CpuMs,
    double MeanMicroseconds,
    long AllocatedBytes,
    double AllocatedBytesPerOperation,
    int SuccessCount,
    bool EquivalentToCurrent,
    string Notes);

internal sealed record BenchmarkConfig(
    string OutputPath,
    string BenchmarkRunId,
    int Iterations,
    int WarmupIterations)
{
    public static BenchmarkConfig Parse(string[] args)
    {
        var output = Path.Combine("artifacts", "runtime-benchmarks", "lifeblood-json-parser-benchmark.json");
        var runId = Guid.NewGuid().ToString("N");
        var iterations = 10_000;
        var warmup = 1_000;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    output = args[++i];
                    break;
                case "--benchmarkRunId":
                    runId = args[++i];
                    break;
                case "--iterations":
                    iterations = int.Parse(args[++i]);
                    break;
                case "--warmup":
                    warmup = int.Parse(args[++i]);
                    break;
            }
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "Iterations must be positive.");
        }

        if (warmup < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmup), "Warmup iterations must be non-negative.");
        }

        return new BenchmarkConfig(output, runId, iterations, warmup);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
