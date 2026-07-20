# AsyncSharp

[![NuGet](https://img.shields.io/nuget/v/AsyncSharp)](https://www.nuget.org/packages/AsyncSharp)
[![NuGet](https://img.shields.io/nuget/dt/AsyncSharp)](https://www.nuget.org/packages/AsyncSharp)

A collection of async-friendly resource-control primitives, including **AsyncSemaphore**, **AsyncMutex**, and **ReadersWriterAsyncLock**. Every class provides synchronous and asynchronous acquisition APIs with cancellation-token support. `AsyncSemaphore` and `AsyncMutex` also expose timeout overloads; `ReadersWriterAsyncLock` uses cancellation tokens to bound acquisition time.

Performance comparisons against the .NET base class library, Nito.AsyncEx, and Microsoft.VisualStudio.Threading are available in the [benchmark project](AsyncSharp.Benchmarks/README.md), with the latest curated run in [RESULTS.md](AsyncSharp.Benchmarks/RESULTS.md).

## AsyncSemaphore
AsyncSemaphore provides similar functionality to SemaphoreSlim, along with the ability to acquire more than 1 count in a single operation, to release all at once, optional fairness (for both synchronous and asynchronous operations together), and optional disposable acquire and release operations. Disposing a semaphore faults pending waiters with `ObjectDisposedException`, and all later operations throw the same exception.

`ReleaseAll` completes every waiter currently in the queue and resets `CurrentCount`. A disposable lease granted by, or made stale by, that reset can still be disposed safely; it will not over-release the reset semaphore. Code that manually pairs `Wait` with `Release` remains responsible for reconciling its own releases across a `ReleaseAll` reset.

Below are examples of the three classes available and some of their methods being used.

 * Async locking example:
```csharp
using var semaphore = new AsyncSemaphore(1, 1);
await semaphore.WaitAsync();
try 
{
    // Your operation
}
finally
{
    semaphore.Release();
}
```

 * Synchronous locking example:
```csharp
using var semaphore = new AsyncSemaphore(1, 1);
semaphore.Wait();
try 
{
    // Your operation
}
finally
{
    semaphore.Release();
}
```

 * Disposable locking example:
```csharp
using var semaphore = new AsyncSemaphore(1, 1);
using (await semaphore.WaitAndReleaseAsync())
{
    // Your operation
}
```

 * Acquire example:
```csharp
using var semaphore = new AsyncSemaphore(5, 5);
await semaphore.WaitAsync(2);
try 
{
    // Your operation
}
finally
{
    semaphore.Release(2);
}
```

 * Acquire with fairness example:
```csharp
using var semaphore = new AsyncSemaphore(5, 5, true);
await semaphore.WaitAsync(2);
try 
{
    // Your operation
}
finally
{
    semaphore.Release(2);
}
```

 * Throttling example:
```csharp
using var semaphore = new AsyncSemaphore(10, 10);
using var cancellationTokenSource = new CancellationTokenSource();
_ = Task.Run(async () => 
{
    // In a background task, release up to 10 per second
    while (!cancellationTokenSource.IsCancellationRequested)
    {
        await Task.Delay(1000);
        semaphore.ReleaseUpTo(10);
    }
});

while (!cancellationTokenSource.IsCancellationRequested)
{
    // This restricts the DoHeavyThrottledOperation to a maximum of 10/second
    var throttledAmountAvailable = semaphore.AcquireUpTo(10);
    await DoHeavyThrottledOperation(throttledAmountAvailable);
}
```

## AsyncMutex
AsyncMutex provides similar functionality to AsyncSemaphore, but only allows for an exclusive acquire of the mutex (similar as a traditional mutex/lock).

 * Async locking example:
```csharp
using var mutex = new AsyncMutex();
await mutex.LockAsync();
try 
{
    // Your operation
}
finally
{
    mutex.Unlock();
}
```

 * Synchronous locking example:
```csharp
using var mutex = new AsyncMutex();
mutex.Lock();
try 
{
    // Your operation
}
finally
{
    mutex.Unlock();
}
```

 * Disposable locking example:
```csharp
using var mutex = new AsyncMutex();
using (await mutex.LockAndUnlockAsync())
{
    // Your operation
}
```


## ReadersWriterAsyncLock
Provides an async-friendly readers-writer lock with optional fairness, a configurable positive maximum reader count, and upgradeable reader ownership. Multiple ordinary readers may coexist, while a writer has exclusive access.

Only one upgradeable reader owner is admitted at a time. That owner may upgrade to a writer after existing ordinary readers leave, without deadlocking behind an already queued writer. A second upgrade attempt on the same owner while an upgrade is pending or active throws `InvalidOperationException`; a canceled attempt may be retried. Dispose the upgraded-writer lease before disposing its outer upgradeable-reader lease. Disposing the outer lease while an upgrade is pending or active throws `InvalidOperationException`, after which disposal can be retried once the upgrade has ended. All returned leases are safe to dispose more than once.

* Acquire a reader lock:
```csharp
using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
using (var readerLock = await readersWriterAsyncLock.AcquireReaderAsync())
{
    // Do operations while holding reader lock
}
```

* Acquire a writer lock:
```csharp
using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
using (var writerLock = await readersWriterAsyncLock.AcquireWriterAsync())
{
    // Do operations while holding exclusive writer lock
}
```

 * Acquiring a reader lock and upgrading to a writer lock example:
```csharp
using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
using (var upgradeableLock = await readersWriterAsyncLock.AcquireUpgradeableReaderAsync())
{
    // Do operations while holding reader lock
    using (var writerLock = await upgradeableLock.UpgradeToWriterAsync())
    {
        // Do operations while holding writer lock
    }
    // Finish any operations with reader lock
}
```
