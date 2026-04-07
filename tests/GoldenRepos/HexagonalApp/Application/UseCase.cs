using HexagonalApp.Domain;

namespace HexagonalApp.Application;

public class CreateEntityUseCase
{
    private readonly IRepository _repo;

    public CreateEntityUseCase(IRepository repo)
    {
        _repo = repo;
    }

    public Entity Execute(string name)
    {
        var entity = new Entity { Id = Guid.NewGuid().ToString(), Name = name };
        _repo.Save(entity);
        return entity;
    }
}
