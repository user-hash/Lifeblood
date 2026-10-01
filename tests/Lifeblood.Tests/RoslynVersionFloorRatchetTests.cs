using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-ROSLYN-FLOOR-001: the Microsoft.CodeAnalysis(.CSharp) the process
/// loads must stay at or above the qualified package floor. Parser support
/// and operation shapes are pinned separately by CSharp14SemanticsTests;
/// feature-switch plumbing alone does not establish compiler support.
/// </summary>
public class RoslynVersionFloorRatchetTests
{
    private const int FloorMajor = 5;
    private const int FloorMinor = 9;

    [Fact]
    public void LoadedRoslyn_MeetsQualifiedFloor()
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
            $"Roslyn must be >= {FloorMajor}.{FloorMinor} for qualified language support; loaded '{info}'.");
    }
}
