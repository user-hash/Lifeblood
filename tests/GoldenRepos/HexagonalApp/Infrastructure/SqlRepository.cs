using HexagonalApp.Domain;

namespace HexagonalApp.Infrastructure;

public class SqlRepository : IRepository
{
    private readonly Dictionary<string, Entity> _store = new();

    public Entity? GetById(string id)
    {
        return _store.TryGetValue(id, out var entity) ? entity : null;
    }

    public void Save(Entity entity)
    {
        _store[entity.Id] = entity;
    }
}
