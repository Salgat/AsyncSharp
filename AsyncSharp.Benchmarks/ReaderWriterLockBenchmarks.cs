using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using NitoReaderWriterLock = Nito.AsyncEx.AsyncReaderWriterLock;
using SharpReaderWriterLock = global::AsyncSharp.ReadersWriterAsyncLock;
using VsReaderWriterLock = Microsoft.VisualStudio.Threading.AsyncReaderWriterLock;

namespace AsyncSharp.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ReaderWriterUncontendedBenchmarks
{
    private const int Operations = 256;

    private readonly ReaderWriterLockSlim _bcl = new();
    private readonly NitoReaderWriterLock _nito = new();
    private readonly SharpReaderWriterLock _sharp = new();
    private readonly VsReaderWriterLock _vs = new();

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync read")]
    public void Bcl_SyncRead()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _bcl.EnterReadLock();
            _bcl.ExitReadLock();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync read")]
    public void Nito_SyncRead()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (_nito.ReaderLock())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync read")]
    public void AsyncSharp_SyncRead()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (_sharp.AcquireReader())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync write")]
    public void Bcl_SyncWrite()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _bcl.EnterWriteLock();
            _bcl.ExitWriteLock();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync write")]
    public void Nito_SyncWrite()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (_nito.WriterLock())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync write")]
    public void AsyncSharp_SyncWrite()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (_sharp.AcquireWriter())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async read")]
    public async Task Nito_AsyncRead()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _nito.ReaderLockAsync())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async read")]
    public async Task AsyncSharp_AsyncRead()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _sharp.AcquireReaderAsync().ConfigureAwait(false))
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async read")]
    public async Task VsThreading_AsyncRead()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _vs.ReadLockAsync())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async write")]
    public async Task Nito_AsyncWrite()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _nito.WriterLockAsync())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async write")]
    public async Task AsyncSharp_AsyncWrite()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _sharp.AcquireWriterAsync().ConfigureAwait(false))
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async write")]
    public async Task VsThreading_AsyncWrite()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _vs.WriteLockAsync())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync upgrade")]
    public void Bcl_SyncUpgrade()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _bcl.EnterUpgradeableReadLock();
            try
            {
                _bcl.EnterWriteLock();
                _bcl.ExitWriteLock();
            }
            finally
            {
                _bcl.ExitUpgradeableReadLock();
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync upgrade")]
    public void AsyncSharp_SyncUpgrade()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (var upgradeable = _sharp.AcquireUpgradeableReader())
            using (upgradeable.UpgradeToWriter())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async upgrade")]
    public async Task AsyncSharp_AsyncUpgrade()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (var upgradeable = await _sharp.AcquireUpgradeableReaderAsync().ConfigureAwait(false))
            using (await upgradeable.UpgradeToWriterAsync().ConfigureAwait(false))
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async upgrade")]
    public async Task VsThreading_AsyncUpgrade()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _vs.UpgradeableReadLockAsync())
            using (await _vs.WriteLockAsync())
            {
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bcl.Dispose();
        _sharp.Dispose();
        _vs.Dispose();
    }
}

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ReaderWriterContentionBenchmarks
{
    private readonly NitoReaderWriterLock _nito = new();
    private readonly SharpReaderWriterLock _sharp = new();
    private readonly VsReaderWriterLock _vs = new();

    [Params(16, 64)]
    public int WaiterCount { get; set; }

    [Benchmark]
    [BenchmarkCategory("Readers after writer")]
    public async Task Nito_ReadersAfterWriter()
    {
        var owner = await _nito.WriterLockAsync();
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseNitoReaderOnceAsync();
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Readers after writer")]
    public async Task AsyncSharp_ReadersAfterWriter()
    {
        var owner = await _sharp.AcquireWriterAsync().ConfigureAwait(false);
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseAsyncSharpReaderOnceAsync();
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Readers after writer")]
    public async Task VsThreading_ReadersAfterWriter()
    {
        var owner = await _vs.WriteLockAsync();
        var waiters = new Task[WaiterCount];
        using (_vs.HideLocks())
        {
            for (var i = 0; i < waiters.Length; ++i)
            {
                waiters[i] = UseVsThreadingReaderOnceAsync();
            }
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Writers after reader")]
    public async Task Nito_WritersAfterReader()
    {
        var owner = await _nito.ReaderLockAsync();
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseNitoWriterOnceAsync();
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Writers after reader")]
    public async Task AsyncSharp_WritersAfterReader()
    {
        var owner = await _sharp.AcquireReaderAsync().ConfigureAwait(false);
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseAsyncSharpWriterOnceAsync();
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Writers after reader")]
    public async Task VsThreading_WritersAfterReader()
    {
        var owner = await _vs.ReadLockAsync();
        var waiters = new Task[WaiterCount];
        using (_vs.HideLocks())
        {
            for (var i = 0; i < waiters.Length; ++i)
            {
                waiters[i] = UseVsThreadingWriterOnceAsync();
            }
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    private async Task UseNitoReaderOnceAsync()
    {
        using (await _nito.ReaderLockAsync())
        {
        }
    }

    private async Task UseAsyncSharpReaderOnceAsync()
    {
        using (await _sharp.AcquireReaderAsync().ConfigureAwait(false))
        {
        }
    }

    private async Task UseVsThreadingReaderOnceAsync()
    {
        using (await _vs.ReadLockAsync())
        {
        }
    }

    private async Task UseNitoWriterOnceAsync()
    {
        using (await _nito.WriterLockAsync())
        {
        }
    }

    private async Task UseAsyncSharpWriterOnceAsync()
    {
        using (await _sharp.AcquireWriterAsync().ConfigureAwait(false))
        {
        }
    }

    private async Task UseVsThreadingWriterOnceAsync()
    {
        using (await _vs.WriteLockAsync())
        {
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sharp.Dispose();
        _vs.Dispose();
    }
}
