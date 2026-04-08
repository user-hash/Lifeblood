using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Rules;

namespace Lifeblood.CLI;

/// <summary>
/// Resolves rules from built-in pack names or file paths.
/// Built-in resolution delegates to <see cref="Analysis.RulePacks"/>.
/// File I/O stays here (composition root owns I/O).
/// </summary>
internal sealed class RulesLoader : IRuleProvider
{
    private readonly IFileSystem _fs;

    public RulesLoader(IFileSystem fs) => _fs = fs;

    public ArchitectureRule[] LoadRules(string nameOrPath)
    {
        // Built-in pack? (e.g. "hexagonal", "clean-architecture", "lifeblood")
        var builtIn = Analysis.RulePacks.ResolveBuiltIn(nameOrPath);
        if (builtIn != null) return builtIn;

        // File path — must exist (typos should fail loudly)
        if (!_fs.FileExists(nameOrPath))
            throw new FileNotFoundException(
                $"'{nameOrPath}' is not a built-in rule pack ({string.Join(", ", Analysis.RulePacks.BuiltIn)}) and file was not found.",
                nameOrPath);

        var json = _fs.ReadAllText(nameOrPath);
        return Analysis.RulePacks.ParseJson(json) ?? Array.Empty<ArchitectureRule>();
    }
}
