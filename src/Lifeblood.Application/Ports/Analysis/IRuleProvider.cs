using Lifeblood.Domain.Rules;

namespace Lifeblood.Application.Ports.Analysis;

public interface IRuleProvider
{
    ArchitectureRule[] LoadRules(string path);
}
