using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-MULTI-DEFINE-RESOLVER-001 — Wave 6.A.</summary>
public class DefineProfileResolverTests
{
    [Fact]
    public void DefaultResolver_ReturnsExactlyOneEditorProfile()
    {
        var resolver = new DefaultDefineProfileResolver();

        var profiles = resolver.ResolveProfiles("/any/path");

        var profile = Assert.Single(profiles);
        Assert.Equal(DefaultDefineProfileResolver.EditorProfileName, profile.Name);
        Assert.Equal("Editor", profile.Name);
    }

    [Fact]
    public void DefaultResolver_EditorProfile_IsIdentity_AddAndRemoveAreEmpty()
    {
        var resolver = new DefaultDefineProfileResolver();

        var profile = resolver.ResolveProfiles("/any/path").Single();

        Assert.Empty(profile.AddDefines);
        Assert.Empty(profile.RemoveDefines);
    }

    [Fact]
    public void DefaultResolver_IsIdempotent_RepeatedCallsReturnSameShape()
    {
        var resolver = new DefaultDefineProfileResolver();

        var first = resolver.ResolveProfiles("/path/one");
        var second = resolver.ResolveProfiles("/path/two");

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first[0].Name, second[0].Name);
    }

    [Fact]
    public void DefineProfile_Fields_AreImmutableAfterConstruction()
    {
        var add = new[] { "X" };
        var remove = new[] { "Y" };
        var profile = new DefineProfile { Name = "P", AddDefines = add, RemoveDefines = remove };

        Assert.Equal("P", profile.Name);
        Assert.Equal(new[] { "X" }, profile.AddDefines);
        Assert.Equal(new[] { "Y" }, profile.RemoveDefines);
    }
}
