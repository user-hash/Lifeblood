using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.UseCases;

namespace Lifeblood.CLI;

internal static class ApplyFileCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
    };

    internal static int Run(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length != 8)
                throw new ArgumentException("Usage: lifeblood apply-file --project <root> --target <path> --receipt <receipt.json> --replacement <staged-file>");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (args[i] is not ("--project" or "--target" or "--receipt" or "--replacement")
                    || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    || !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("Exactly one --project, --target, --receipt and --replacement is required.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var receiptStream = File.OpenRead(options["--receipt"]);
            if (receiptStream.Length > 128 * 1024) throw new ArgumentException("Receipt exceeds 128 KiB.");
            var receipt = JsonSerializer.Deserialize<FileScopeReceipt>(receiptStream, Json)
                ?? throw new ArgumentException("A file-scope receipt object is required.");
            if (receipt.Files == null || receipt.Files.Any(file => file == null || string.IsNullOrWhiteSpace(file.FilePath)))
                throw new ArgumentException("Receipt files must contain non-null path expectations.");
            var staged = ConditionalFileWriter.ReadReplacement(options["--replacement"], cancellationToken);
            var result = new ConditionalFileWriter().Apply(options["--project"], options["--target"], receipt, staged, cancellationToken);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, action = "applyFile", result.Success, result.Status, result.Receipt, result.Conflicts,
                semanticInputsVerified = false,
                limitations = new[] { "Single existing-file conditional write under Windows sharing locks; not a crash-atomic transaction. Receipt is an observation at commit, not a lasting reservation. Caller must obtain any project-specific source admission." },
            }, Json));
            return result.Success ? 0 : 2;
        }
        catch (ConditionalFileWriteException ex)
        {
            return Fail("file-apply-write-failed", ex.Message, 4, ex.OriginalContentRestored ? "originalRestored" : "unknown");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or KeyNotFoundException or PlatformNotSupportedException)
        {
            return Fail("file-apply-contract", ex.Message, 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("file-apply-unavailable", ex.Message, 3);
        }
        catch (OperationCanceledException) { return Fail("file-apply-canceled", "Canceled before commit.", 130); }

        int Fail(string failure, string message, int code, string writeState = "notStarted")
        {
            output.WriteLine(JsonSerializer.Serialize(new { success = false, failure, message, writeState }, Json));
            return code;
        }
    }
}
