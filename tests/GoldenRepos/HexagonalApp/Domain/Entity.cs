namespace HexagonalApp.Domain;

public class Entity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public interface IRepository
{
    Entity? GetById(string id);
    void Save(Entity entity);
}
