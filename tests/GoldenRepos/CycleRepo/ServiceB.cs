namespace CycleRepo;

public class ServiceB
{
    private readonly ServiceA _a;

    public ServiceB(ServiceA a)
    {
        _a = a;
    }

    public void Process() => _a.DoWork();
}
