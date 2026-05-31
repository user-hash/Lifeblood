using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MCP-SESSION-GATE-001. Shared-server concurrency safety lives at the
/// MCP host boundary, where session replacement can be serialized without
/// contaminating Domain/Application code with locks.
/// </summary>
public class GraphSessionGateTests
{
    [Fact]
    public async Task Read_AllowsConcurrentReaders()
    {
        using var gate = new GraphSessionGate();
        using var release = new ManualResetEventSlim(false);
        var inside = 0;
        var maxInside = 0;

        int ReadBody()
        {
            var current = Interlocked.Increment(ref inside);
            UpdateMax(ref maxInside, current);
            release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref inside);
            return 1;
        }

        var first = Task.Run(() => gate.Read(ReadBody));
        var second = Task.Run(() => gate.Read(ReadBody));

        SpinWait.SpinUntil(() => Volatile.Read(ref inside) == 2, TimeSpan.FromSeconds(5));
        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(2, maxInside);
    }

    [Fact]
    public async Task Write_WaitsForActiveReader()
    {
        using var gate = new GraphSessionGate();
        using var readerEntered = new ManualResetEventSlim(false);
        using var releaseReader = new ManualResetEventSlim(false);
        var writeEntered = 0;

        var read = Task.Run(() => gate.Read(() =>
        {
            readerEntered.Set();
            releaseReader.Wait(TimeSpan.FromSeconds(5));
            return 1;
        }));

        Assert.True(readerEntered.Wait(TimeSpan.FromSeconds(5)));
        var write = Task.Run(() => gate.Write(() =>
        {
            Interlocked.Exchange(ref writeEntered, 1);
            return 1;
        }));

        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref writeEntered));

        releaseReader.Set();
        await Task.WhenAll(read, write);
        Assert.Equal(1, Volatile.Read(ref writeEntered));
    }

    [Fact]
    public async Task Read_WaitsForActiveWriter()
    {
        using var gate = new GraphSessionGate();
        using var writerEntered = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        var readEntered = 0;

        var write = Task.Run(() => gate.Write(() =>
        {
            writerEntered.Set();
            releaseWriter.Wait(TimeSpan.FromSeconds(5));
            return 1;
        }));

        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));
        var read = Task.Run(() => gate.Read(() =>
        {
            Interlocked.Exchange(ref readEntered, 1);
            return 1;
        }));

        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref readEntered));

        releaseWriter.Set();
        await Task.WhenAll(read, write);
        Assert.Equal(1, Volatile.Read(ref readEntered));
    }

    [Fact]
    public async Task Write_AllowsOnlyOneActiveWriter()
    {
        using var gate = new GraphSessionGate();
        using var firstWriterEntered = new ManualResetEventSlim(false);
        using var releaseFirstWriter = new ManualResetEventSlim(false);
        var inside = 0;
        var maxInside = 0;

        int WriteBody(ManualResetEventSlim? entered = null)
        {
            var current = Interlocked.Increment(ref inside);
            UpdateMax(ref maxInside, current);
            entered?.Set();
            releaseFirstWriter.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref inside);
            return 1;
        }

        var first = Task.Run(() => gate.Write(() => WriteBody(firstWriterEntered)));
        Assert.True(firstWriterEntered.Wait(TimeSpan.FromSeconds(5)));

        var second = Task.Run(() => gate.Write(() => WriteBody()));
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref maxInside));

        releaseFirstWriter.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maxInside);
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, current) != current);
    }
}
