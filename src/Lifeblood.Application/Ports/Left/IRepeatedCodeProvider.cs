using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>Request-local repeated-body evidence over one retained publication.</summary>
public interface IRepeatedCodeProvider
{
    RepeatedCodeReport FindRepeatedCode(RepeatedCodeQuery query, CancellationToken cancellationToken = default);
}
