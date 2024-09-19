# AsyncSharp

[![NuGet](https://img.shields.io/nuget/v/AsyncSharp)](https://www.nuget.org/packages/AsyncSharp)
[![NuGet](https://img.shields.io/nuget/dt/AsyncSharp)](https://www.nuget.org/packages/AsyncSharp)

A collection of async friendly classes for resource control, including **AsyncSemaphore**, **AsyncMutex**, and **ReadersWriterAsyncLock**. All classes provide both synchronous and asynchronous methods that expose both timeouts and cancellation token support.

## AsyncSemaphore
AsyncSemaphore provides similar functionality to SemaphoreSlim, along with the ability to acquire more than 1 count in a single operation, to release all at once, optional fairness (for both synchronous and asynchronous operations together), and optional disposable acquire and release operations. Below are examples of the three classes available and some of their methods being used.

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
Provides a readers-writer lock that is both async friendly, allows for optional fairness, optional max number of readers, and optional upgradeable locks. This allows for multiple readers, while acquiring the writer lock creates exclusive access (meaning no other readers or writers).

* Acquire a reader lock:
```csharp
using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
using (var readerLock = await readersWriterAsyncLock.AcquireReader())
{
    // Do operations while holding reader lock
}
```

* Acquire a writer lock:
```csharp
using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
using (var writerLock = await readersWriterAsyncLock.AcquireWriter())
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