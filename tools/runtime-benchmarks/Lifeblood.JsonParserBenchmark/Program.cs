using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var config = BenchmarkConfig.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.OutputPath))!);

var payload = """
{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"lifeblood_analyze","arguments":{"projectPath":"D:/Projekti/Lifeblood","incremental":true,"allowFullFallback":true,"defineProfiles":["Editor","Player"]}}}
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
        "source-generated JSON contexts remain gated on Lifeblood diagnostic parity for generator-created members.",
    },
    variants,
};

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(config.OutputPath, JsonSerializer.Serialize(report, jsonOptions), Encoding.UTF8);
Console.WriteLine($"Wrote JSON parser benchmark report to {config.OutputPath}");

static JsonRpcRequest? CurrentString(string payload, bool strictJson)
    => McpJsonRequestParser.DeserializeRequest(payload, JsonOptions.Default, strictJson);

static JsonRpcRequest? Utf8Span(byte[] payloadBytes, bool strictJson)
{
    if (strictJson)
    {
        DuplicatePropertyGuard.ThrowIfDuplicateProperties(payloadBytes);
    }

    return JsonSerializer.Deserialize<JsonRpcRequest>(
        payloadBytes,
        strictJson ? JsonOptions.Strict : JsonOptions.Default);
}

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
        var span = sequence.FirstSpan;
        if (strictJson)
        {
            DuplicatePropertyGuard.ThrowIfDuplicateProperties(span);
        }

        return JsonSerializer.Deserialize<JsonRpcRequest>(
            span,
            strictJson ? JsonOptions.Strict : JsonOptions.Default);
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

    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
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
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    return new VariantResult(
        name,
        parser,
        strictJson,
        sw.Elapsed.TotalMilliseconds,
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Strict = new(Default)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

internal static class McpJsonRequestParser
{
    public static JsonRpcRequest? DeserializeRequest(
        string json,
        JsonSerializerOptions options,
        bool strictJson)
    {
        if (strictJson)
        {
            DuplicatePropertyGuard.ThrowIfDuplicateProperties(Encoding.UTF8.GetBytes(json));
            return JsonSerializer.Deserialize<JsonRpcRequest>(json, JsonOptions.Strict);
        }

        return JsonSerializer.Deserialize<JsonRpcRequest>(json, options);
    }
}

internal static class DuplicatePropertyGuard
{
    public static void ThrowIfDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (scopes.Count > 0)
                    {
                        scopes.Pop();
                    }
                    break;
                case JsonTokenType.PropertyName:
                    if (scopes.Count == 0)
                    {
                        break;
                    }

                    var propertyName = reader.GetString() ?? "";
                    if (!scopes.Peek().Add(propertyName))
                    {
                        throw new JsonException(
                            $"Duplicate JSON property '{propertyName}' is not allowed when strict JSON mode is enabled.");
                    }

                    break;
            }
        }
    }
}
