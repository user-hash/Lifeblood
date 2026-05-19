using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchets for the native Clang adapter's hexagonal boundary. The native
/// adapter is intentionally outside the managed Lifeblood core: LLVM/libclang
/// stays under adapters/native-clang, and the only integration contract is the
/// JSON graph schema consumed by JsonGraphImporter.
/// </summary>
public class NativeClangArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string NativeRoot = Path.Combine(RepoRoot, "adapters", "native-clang");
    private static readonly string NativeSrcRoot = Path.Combine(NativeRoot, "src");

    private static readonly string[] LibClangTokens =
    {
        "#include <clang-c/",
        "CXCompilationDatabase",
        "CXCompileCommand",
        "CXCompileCommands",
        "CXCursor",
        "CXDiagnostic",
        "CXErrorCode",
        "CXEvalResult",
        "CXFile",
        "CXIndex",
        "CXSourceLocation",
        "CXSourceRange",
        "CXString",
        "CXTranslationUnit",
        "CXType",
        "clang_",
    };

    private static readonly string[] ManagedLifebloodModuleTokens =
    {
        "Lifeblood.Domain",
        "Lifeblood.Application",
        "Lifeblood.Analysis",
        "Lifeblood.Adapters.CSharp",
        "Lifeblood.Adapters.JsonGraph",
        "Lifeblood.Connectors.",
        "Lifeblood.Server.Mcp",
        "Lifeblood.CLI",
    };

    [Fact]
    public void NativeClang_LibClangApiUsageStaysInsideExplicitBoundary()
    {
        // INV-NATIVE-CLANG-LIBCLANG-001. This allowlist is a deliberate
        // transition fence: current beta emitters still consume CXCursor
        // directly, but no NEW raw libclang touch point may appear without
        // a conscious ratchet edit. N2 should shrink this list by moving
        // inner emitters to native fact DTOs.
        string[] expected =
        {
            "adapters/native-clang/src/ClangCommandLineMacroCollector.cpp",
            "adapters/native-clang/src/ClangCommandLineMacroCollector.h",
            "adapters/native-clang/src/ClangCompilationDatabase.cpp",
            "adapters/native-clang/src/ClangCompilationDatabase.h",
            "adapters/native-clang/src/ClangCompileCommandReader.cpp",
            "adapters/native-clang/src/ClangCompileCommandReader.h",
            "adapters/native-clang/src/ClangIndex.cpp",
            "adapters/native-clang/src/ClangIndex.h",
            "adapters/native-clang/src/ClangParseArgumentBuilder.cpp",
            "adapters/native-clang/src/ClangParseArgumentBuilder.h",
            "adapters/native-clang/src/ClangSourceMapper.cpp",
            "adapters/native-clang/src/ClangSourceMapper.h",
            "adapters/native-clang/src/ClangTranslationUnitParser.cpp",
            "adapters/native-clang/src/ClangTranslationUnitParser.h",
            "adapters/native-clang/src/ClangUtilities.cpp",
            "adapters/native-clang/src/ClangUtilities.h",
            "adapters/native-clang/src/NativeAstVisitor.cpp",
            "adapters/native-clang/src/NativeAstVisitor.h",
            "adapters/native-clang/src/NativeCursorHandle.h",
            "adapters/native-clang/src/NativeExtractionSession.cpp",
            "adapters/native-clang/src/NativeExtractionSession.h",
            "adapters/native-clang/src/NativeFunctionEmitter.cpp",
            "adapters/native-clang/src/NativeFunctionEmitter.h",
            "adapters/native-clang/src/NativeGlobalEmitter.cpp",
            "adapters/native-clang/src/NativeGlobalEmitter.h",
            "adapters/native-clang/src/NativeIncludeEmitter.cpp",
            "adapters/native-clang/src/NativeIncludeEmitter.h",
            "adapters/native-clang/src/NativeMacroEmitter.cpp",
            "adapters/native-clang/src/NativeMacroEmitter.h",
            "adapters/native-clang/src/NativePreprocessorEmitter.cpp",
            "adapters/native-clang/src/NativePreprocessorEmitter.h",
            "adapters/native-clang/src/NativeReferenceEdgeWriter.cpp",
            "adapters/native-clang/src/NativeReferenceEdgeWriter.h",
            "adapters/native-clang/src/NativeReferenceEmitter.cpp",
            "adapters/native-clang/src/NativeReferenceEmitter.h",
            "adapters/native-clang/src/NativeSymbolIds.cpp",
            "adapters/native-clang/src/NativeSymbolIds.h",
            "adapters/native-clang/src/NativeTableRowEmitter.cpp",
            "adapters/native-clang/src/NativeTableRowEmitter.h",
            "adapters/native-clang/src/NativeTypeEmitter.cpp",
            "adapters/native-clang/src/NativeTypeEmitter.h",
            "adapters/native-clang/src/NativeTypeMemberEmitter.cpp",
            "adapters/native-clang/src/NativeTypeMemberEmitter.h",
        };

        var actual = NativeSourceFiles()
            .Where(ContainsLibClangToken)
            .Select(RelativeRepoPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actual);
    }

    [Fact]
    public void NativeClang_SourceAndBuildFilesDoNotReferenceManagedLifebloodModules()
    {
        // INV-NATIVE-CLANG-BOUNDARY-001. The native adapter is an external
        // executable. It must not take a source/build dependency on managed
        // Lifeblood projects; JSON graph is the integration boundary.
        var offenders = NativeBoundaryFiles()
            .SelectMany(file =>
            {
                var text = File.ReadAllText(file);
                return ManagedLifebloodModuleTokens
                    .Where(token => text.Contains(token, StringComparison.Ordinal))
                    .Select(token => $"{RelativeRepoPath(file)} contains {token}");
            })
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NativeClang_CMakeLinksOnlyAgainstLibClang()
    {
        var cmakePath = Path.Combine(NativeRoot, "CMakeLists.txt");
        var cmake = File.ReadAllText(cmakePath);

        Assert.Contains("target_link_libraries(lifeblood-native-clang PRIVATE", cmake);
        Assert.Contains("${LIBCLANG_LIBRARY}", cmake);
        foreach (var token in ManagedLifebloodModuleTokens)
            Assert.DoesNotContain(token, cmake, StringComparison.Ordinal);
    }

    private static IEnumerable<string> NativeSourceFiles()
        => Directory.EnumerateFiles(NativeSrcRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".h", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> NativeBoundaryFiles()
        => NativeSourceFiles().Concat(new[] { Path.Combine(NativeRoot, "CMakeLists.txt") });

    private static bool ContainsLibClangToken(string path)
    {
        var text = File.ReadAllText(path);
        return LibClangTokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    private static string RelativeRepoPath(string path)
        => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static string FindRepoRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Lifeblood.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate Lifeblood.sln from test base directory.");
    }
}
