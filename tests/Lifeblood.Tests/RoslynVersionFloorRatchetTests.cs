using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-ROSLYN-FLOOR-001: the Microsoft.CodeAnalysis(.CSharp) the process
/// loads MUST stay at or above 4.14. The 4.12→4.14 bump
/// (<c>d8b25e4</c>) activates runtime-async parse parity — inert under 4.12,
/// where the parser ignored the <c>&lt;Features&gt;runtime-async=on&gt;</c>
/// string entirely. CsprojCompilationFactsTests asserts that feature string
/// reaches <c>CSharpParseOptions.Features</c>, but that plumbing test passes
/// identically under 4.12 (the dict always holds the string; only the parser
/// behaviour differs). So nothing there regression-guards the bump: a silent
/// revert to 4.12 leaves every test green. THIS ratchet is the guard — revert
/// the package pin and it fails loudly, naming the version it found.
/// </summary>
public class RoslynVersionFloorRatchetTests
{
    private const int FloorMajor = 4;
    private const int FloorMinor = 14;

    [Fact]
    public void LoadedRoslyn_IsAtLeast_4_14()
    {
        var asm = typeof(CSharpCompilation).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "";

        var match = Regex.Match(info, @"^(\d+)\.(\d+)");
        Assert.True(match.Success, $"Could not parse a Roslyn version out of '{info}'.");

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);

        Assert.True(
            major > FloorMajor || (major == FloorMajor && minor >= FloorMinor),
            $"Roslyn must be >= {FloorMajor}.{FloorMinor} for runtime-async parse parity; loaded '{info}'. "
                + "If this failed, the Microsoft.CodeAnalysis package pin was reverted below 4.14.");
    }
}
