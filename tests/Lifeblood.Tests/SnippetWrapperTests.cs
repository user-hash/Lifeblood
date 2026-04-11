using Lifeblood.Adapters.CSharp.Internal;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Pins <see cref="SnippetWrapper"/> — the contract decision that turns
/// raw <c>compile_check</c> input into a syntax tree the compiler will
/// accept inside library modules.
///
/// The bug class this guards against: a "snippet" tool that requires its
/// input to already be a complete CompilationUnit. <c>var x = 1 + 1;</c>
/// must Just Work in any module, executable or library, because the
/// public tool description says "snippet" and that's the contract.
/// </summary>
public class SnippetWrapperTests
{
    // ─────────────────────────────────────────────────────────────────────
    // Pass-through cases: snippet already declares a top-level
    // type/namespace/delegate, so we hand it to the compiler unchanged.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("class Foo { }")]
    [InlineData("public class Foo { void M() { } }")]
    [InlineData("namespace N { class Foo { } }")]
    [InlineData("namespace N;\nclass Foo { }")] // file-scoped namespace
    [InlineData("public delegate int Adder(int a, int b);")]
    [InlineData("interface IFoo { void M(); }")]
    [InlineData("struct S { public int X; }")]
    [InlineData("enum E { A, B }")]
    [InlineData("record R(int X);")]
    public void Prepare_TopLevelTypeOrNamespace_PassesThrough(string code)
    {
        var result = SnippetWrapper.Prepare(code);

        Assert.False(result.WasWrapped);
        Assert.Equal(0, result.WrapperLineNumber);
        // Tree is the original parse — no wrapper class injected.
        var root = (CompilationUnitSyntax)result.Tree.GetRoot();
        Assert.DoesNotContain(root.Members.OfType<ClassDeclarationSyntax>(),
            c => c.Identifier.Text == SnippetWrapper.WrapperTypeName);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Wrap cases: snippet has only statements/expressions, must be
    // wrapped so it compiles in a library module.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prepare_BareStatement_Wrapped()
    {
        var result = SnippetWrapper.Prepare("var x = 1 + 1;");

        Assert.True(result.WasWrapped);
        Assert.Equal(1, result.WrapperLineNumber);
        var root = (CompilationUnitSyntax)result.Tree.GetRoot();
        var probe = Assert.Single(root.Members.OfType<ClassDeclarationSyntax>(),
            c => c.Identifier.Text == SnippetWrapper.WrapperTypeName);
        var method = Assert.Single(probe.Members.OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.Text == SnippetWrapper.WrapperMethodName);
        Assert.NotNull(method.Body);
    }

    [Fact]
    public void Prepare_MultipleStatements_Wrapped()
    {
        var result = SnippetWrapper.Prepare("var x = 1;\nvar y = 2;\nvar z = x + y;");

        Assert.True(result.WasWrapped);
        Assert.Equal(1, result.WrapperLineNumber);
    }

    [Fact]
    public void Prepare_StatementsWithUsings_WrappedWithUsingsPreservedAtTopLevel()
    {
        // Using directives must stay at the top level — they're invalid
        // inside a method body. The wrapper opening sits on the line right
        // after the last using.
        var code = "using System;\nvar x = Console.ReadLine();";
        var result = SnippetWrapper.Prepare(code);

        Assert.True(result.WasWrapped);
        // 1 line of usings → wrapper opening on line 2.
        Assert.Equal(2, result.WrapperLineNumber);

        var root = (CompilationUnitSyntax)result.Tree.GetRoot();
        // The original using directive still exists at the top level.
        Assert.Contains(root.Usings, u => u.Name?.ToString() == "System");
        // And the synthetic class is present alongside it.
        Assert.Contains(root.Members.OfType<ClassDeclarationSyntax>(),
            c => c.Identifier.Text == SnippetWrapper.WrapperTypeName);
    }

    [Fact]
    public void Prepare_StatementsWithMultipleUsings_WrapperLineCountedCorrectly()
    {
        var code = "using System;\nusing System.Collections.Generic;\nusing System.Linq;\nvar nums = new List<int>{1,2,3};";
        var result = SnippetWrapper.Prepare(code);

        Assert.True(result.WasWrapped);
        // 3 using lines → wrapper opening on line 4.
        Assert.Equal(4, result.WrapperLineNumber);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Line remapping: diagnostics inside the wrapped tree must remap
    // back to the user's original line numbers.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MapLineToUser_Unwrapped_ReturnsInputUnchanged()
    {
        var result = SnippetWrapper.Prepare("class Foo { }");
        Assert.Equal(1, SnippetWrapper.MapLineToUser(in result, 1));
        Assert.Equal(5, SnippetWrapper.MapLineToUser(in result, 5));
        Assert.Equal(42, SnippetWrapper.MapLineToUser(in result, 42));
    }

    [Fact]
    public void MapLineToUser_WrappedWithoutUsings_BodyLinesShiftUpByOne()
    {
        // Wrapped:
        //   line 1: class _LifebloodCompileProbe { void _LifebloodCompileBody() {
        //   line 2: var x = 1;       <- user's original line 1
        //   line 3: var y = 2;       <- user's original line 2
        //   line 4: } }
        var result = SnippetWrapper.Prepare("var x = 1;\nvar y = 2;");

        Assert.True(result.WasWrapped);
        Assert.Equal(1, result.WrapperLineNumber);

        // Wrapper opening line maps to itself (caller decides whether to surface).
        Assert.Equal(1, SnippetWrapper.MapLineToUser(in result, 1));
        // Body lines shift up by one.
        Assert.Equal(1, SnippetWrapper.MapLineToUser(in result, 2));
        Assert.Equal(2, SnippetWrapper.MapLineToUser(in result, 3));
    }

    [Fact]
    public void MapLineToUser_WrappedWithUsings_UsingLinesUnchanged_BodyLinesShifted()
    {
        // Wrapped:
        //   line 1: using System;    <- user's original line 1 (unchanged)
        //   line 2: class _Lifeblood... { void ...() {
        //   line 3: var x = Console.ReadLine();   <- user's original line 2
        //   line 4: } }
        var result = SnippetWrapper.Prepare("using System;\nvar x = Console.ReadLine();");

        Assert.True(result.WasWrapped);
        Assert.Equal(2, result.WrapperLineNumber);

        // Lines at or above the wrapper opening are not shifted.
        Assert.Equal(1, SnippetWrapper.MapLineToUser(in result, 1));
        Assert.Equal(2, SnippetWrapper.MapLineToUser(in result, 2));
        // Lines past the wrapper opening shift up by one.
        Assert.Equal(2, SnippetWrapper.MapLineToUser(in result, 3));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pathological inputs: don't crash, don't loop, deterministic output.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prepare_EmptyString_Wrapped_NoCrash()
    {
        var result = SnippetWrapper.Prepare("");
        Assert.True(result.WasWrapped);
        Assert.Equal(1, result.WrapperLineNumber);
    }

    [Fact]
    public void Prepare_OnlyWhitespace_Wrapped_NoCrash()
    {
        var result = SnippetWrapper.Prepare("   \n  \t  ");
        // Whitespace parses as zero members — no top-level type → wrapped.
        Assert.True(result.WasWrapped);
    }

    [Fact]
    public void Prepare_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SnippetWrapper.Prepare(null!));
    }

    // ─────────────────────────────────────────────────────────────────────
    // The wrapped tree must actually parse cleanly — if our wrap shape
    // is broken (mismatched braces, missing semicolons, etc.) the parser
    // emits diagnostics and the compiler chokes downstream.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prepare_BareStatement_WrappedTreeParsesCleanly()
    {
        var result = SnippetWrapper.Prepare("var x = 1 + 1;");
        var diagnostics = result.Tree.GetDiagnostics().ToList();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Prepare_StatementsWithUsings_WrappedTreeParsesCleanly()
    {
        var result = SnippetWrapper.Prepare("using System;\nvar x = Console.ReadLine();");
        var diagnostics = result.Tree.GetDiagnostics().ToList();
        Assert.Empty(diagnostics);
    }
}
