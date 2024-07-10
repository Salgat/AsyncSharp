# AsyncSharp

[![NuGet](https://img.shields.io/nuget/v/AsyncSharp)](https://www.nuget.org/packages/AsyncSharp)
[![NuGet](https://img.shields.io/nuget/dt/AsyncSharp)](https://www.nuget.org/packages/AsyncSharp)

A collection of async friendly classes for resource control, including **AsyncSemaphore**, **AsyncMutex**, and **ReadersWriterAsyncLock**. All classes provide both synchronous and asynchronous methods that expose both timeouts and cancellation token support.

## AsyncSemaphore
AsyncSemaphore provides similar functionality to SemaphoreSlim, along with the ability to acquire more than 1 count in a single operation, to release all at once, optional fairness (for both synchronous and asynchronous operations together), and optional disposable acquire and release operations.

```csharp
var semaphore = new AsyncSemaphore(1, 1, true);
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

## AsyncMutex
AsyncMutex provides similar functionality to AsyncSemaphore, but only allows for an exclusive acquire of the mutex.

```csharp
var mutex = new AsyncMutex();
await semaphore.LockAsync();
try 
{
    // Your operation
}
finally
{
    semaphore.Unlock();
}
```

## ReadersWriterAsyncLock
Provides a readers-writer lock that is both async friendly, allows for optional fairness, optional max number of readers, and optional upgradeable locks.

```csharp
var readersWriterAsyncLock = new ReadersWriterAsyncLock();
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