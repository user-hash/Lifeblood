namespace CycleRepo;

public class ServiceA
{
    private readonly ServiceB _b;

    public ServiceA(ServiceB b)
    {
        _b = b;
    }

    public void DoWork() => _b.Process();
}
