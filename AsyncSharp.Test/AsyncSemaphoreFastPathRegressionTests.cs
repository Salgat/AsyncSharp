using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Regression coverage for optimized acquisition paths. These tests ensure an immediately
    /// satisfiable request cannot bypass queued work except in the explicitly unfair mode, and
    /// exercise count accounting when cancellation wins just before a queued grant or reset.
    /// </summary>
    public class AsyncSemaphoreFastPathRegressionTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RaceTimeout = TimeSpan.FromMilliseconds(20);
        private const int StressIterations = 32;

        [Theory]
        [InlineData(AsyncSemaphore.WaiterPriority.LowToHigh)]
        [InlineData(AsyncSemaphore.WaiterPriority.HighToLow)]
        [InlineData(AsyncSemaphore.WaiterPriority.FirstInFirstOut)]
        [InlineData(AsyncSemaphore.WaiterPriority.FirstInFirstOutUnfair)]
        public async Task SatisfiableAcquire_DoesNotBypassBlockedHigherPriorityBucket(
            AsyncSemaphore.WaiterPriority waiterPriority)
        {
            using var semaphore = new AsyncSemaphore(2, 2, waiterPriority);
            semaphore.Wait(1);
            using var blockedCancellation = new CancellationTokenSource();
            using var followerCancellation = new CancellationTokenSource(Timeout);

            var blocked = semaphore.WaitAsync(
                2,
                AsyncSemaphore.InfiniteTimeSpan,
                priority: 1,
                blockedCancellation.Token);
            var follower = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                priority: 0,
                followerCancellation.Token);

            try
            {
                Assert.Equal(2, semaphore.QueuedWaiterCount);
                Assert.False(follower.IsCompleted);
                Assert.Equal(1, semaphore.CurrentCount);

                blockedCancellation.Cancel();
                var blockedException = await Record.ExceptionAsync(
                    async () => await blocked.WaitAsync(Timeout));

                Assert.IsAssignableFrom<OperationCanceledException>(blockedException);
                Assert.True(await follower.WaitAsync(Timeout));
                semaphore.Release(1);
                semaphore.Release(1);

                Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
                Assert.Equal(0, semaphore.QueuedWaiterCount);
            }
            finally
            {
                blockedCancellation.Cancel();
                followerCancellation.Cancel();
                await ObserveAsync(blocked);
                await ObserveAsync(follower);
            }
        }

        [Fact]
        public async Task Unfair_SatisfiableAcquireMayBypassBlockedHigherPriorityBucket()
        {
            using var semaphore = new AsyncSemaphore(
                2,
                2,
                AsyncSemaphore.WaiterPriority.Unfair);
            semaphore.Wait(1);
            using var blockedCancellation = new CancellationTokenSource();

            var blocked = semaphore.WaitAsync(
                2,
                AsyncSemaphore.InfiniteTimeSpan,
                priority: 1,
                blockedCancellation.Token);
            var follower = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                priority: 0,
                CancellationToken.None);

            try
            {
                Assert.True(await follower.WaitAsync(Timeout));
                Assert.False(blocked.IsCompleted);

                blockedCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await blocked.WaitAsync(Timeout));

                semaphore.Release(1);
                semaphore.Release(1);
                Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
                Assert.Equal(0, semaphore.QueuedWaiterCount);
            }
            finally
            {
                blockedCancellation.Cancel();
                await ObserveAsync(blocked);
            }
        }

        [Fact]
        public async Task PreCanceledToken_DoesNotConsumeImmediatelyAvailableCount()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => semaphore.Wait(1, cancellation.Token));
            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => semaphore.WaitAsync(1, cancellation.Token));
            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task ZeroTimeout_WithImmediatelyAvailableCount_AcquiresAndBalances()
        {
            using var semaphore = new AsyncSemaphore(1, 1);

            Assert.True(semaphore.Wait(1, TimeSpan.Zero));
            Assert.Equal(0, semaphore.CurrentCount);
            semaphore.Release(1);

            Assert.True(await semaphore.WaitAsync(1, TimeSpan.Zero));
            Assert.Equal(0, semaphore.CurrentCount);
            semaphore.Release(1);

            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task PlainWaitAsync_WhenQueued_IsFaultedByDispose()
        {
            var semaphore = new AsyncSemaphore(0, 1);
            var waiter = semaphore.WaitAsync();

            Assert.Equal(1, semaphore.QueuedWaiterCount);
            semaphore.Dispose();

            var exception = await Record.ExceptionAsync(
                async () => await waiter.WaitAsync(Timeout));
            Assert.IsType<ObjectDisposedException>(exception);
            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task PlainWaitAsync_ValidationAndLifecycleFailures_AreDeliveredThroughTask()
        {
            using var invalidSemaphore = new AsyncSemaphore(1, 1);
            Task invalidTask = null;
            var invalidInvocationException = Record.Exception(() =>
            {
                invalidTask = invalidSemaphore.WaitAsync(2);
            });

            Assert.Null(invalidInvocationException);
            Assert.NotNull(invalidTask);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                async () => await invalidTask.WaitAsync(Timeout));
            Assert.True(invalidTask.IsFaulted);
            Assert.Equal(invalidSemaphore.MaxCount, invalidSemaphore.CurrentCount);

            var disposedSemaphore = new AsyncSemaphore(1, 1);
            disposedSemaphore.Dispose();
            Task disposedTask = null;
            var disposedInvocationException = Record.Exception(() =>
            {
                disposedTask = disposedSemaphore.WaitAsync();
            });

            Assert.Null(disposedInvocationException);
            Assert.NotNull(disposedTask);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await disposedTask.WaitAsync(Timeout));
            Assert.True(disposedTask.IsFaulted);

            using var canceledSemaphore = new AsyncSemaphore(1, 1);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Task canceledTask = null;
            var canceledInvocationException = Record.Exception(() =>
            {
                canceledTask = canceledSemaphore.WaitAsync(cancellation.Token);
            });

            Assert.Null(canceledInvocationException);
            Assert.NotNull(canceledTask);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await canceledTask.WaitAsync(Timeout));
            Assert.True(canceledTask.IsCanceled);
            Assert.Equal(canceledSemaphore.MaxCount, canceledSemaphore.CurrentCount);
        }

        [Fact]
        public async Task PlainWaitAsync_CompletesAfterContendedStateLockIsReleased()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var releaseStateLock = new ManualResetEventSlim(false);
            using var invocationStarted = new ManualResetEventSlim(false);
            var stateLockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stateLockHolder = HoldStateLock(
                semaphore,
                stateLockEntered,
                releaseStateLock);

            try
            {
                await stateLockEntered.Task.WaitAsync(Timeout);
                var invocation = Task.Factory.StartNew(
                    () =>
                    {
                        invocationStarted.Set();
                        return semaphore.WaitAsync();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);

                Assert.True(invocationStarted.Wait(Timeout));
                Assert.False(invocation.IsCompleted);
                releaseStateLock.Set();

                var waiter = await invocation.WaitAsync(Timeout);
                await waiter.WaitAsync(Timeout);
            }
            finally
            {
                releaseStateLock.Set();
                await stateLockHolder.WaitAsync(Timeout);
            }

            Assert.Equal(0, semaphore.CurrentCount);
            semaphore.Release(1);
            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
        }

        [Fact]
        public async Task PlainWaitAndReleaseAsync_CompletesAfterContendedStateLockIsReleased()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var releaseStateLock = new ManualResetEventSlim(false);
            using var invocationStarted = new ManualResetEventSlim(false);
            var stateLockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stateLockHolder = HoldStateLock(
                semaphore,
                stateLockEntered,
                releaseStateLock);

            try
            {
                await stateLockEntered.Task.WaitAsync(Timeout);
                var invocation = Task.Factory.StartNew(
                    () =>
                    {
                        invocationStarted.Set();
                        return semaphore.WaitAndReleaseAsync();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);

                Assert.True(invocationStarted.Wait(Timeout));
                Assert.False(invocation.IsCompleted);
                releaseStateLock.Set();

                var leaseTask = await invocation.WaitAsync(Timeout);
                var lease = await leaseTask.WaitAsync(Timeout);
                Assert.Equal(0, semaphore.CurrentCount);

                lease.Dispose();
                lease.Dispose();
            }
            finally
            {
                releaseStateLock.Set();
                await stateLockHolder.WaitAsync(Timeout);
            }

            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task PlainWaitAsync_FaultsAsynchronouslyWhenDisposedDuringStateLockContention()
        {
            var semaphore = new AsyncSemaphore(1, 1);
            using var disposeSemaphore = new ManualResetEventSlim(false);
            using var invocationStarted = new ManualResetEventSlim(false);
            var stateLockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stateLockHolder = DisposeWhileHoldingStateLock(
                semaphore,
                stateLockEntered,
                disposeSemaphore);

            try
            {
                await stateLockEntered.Task.WaitAsync(Timeout);
                var invocation = Task.Factory.StartNew(
                    () =>
                    {
                        invocationStarted.Set();
                        return semaphore.WaitAsync();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);

                Assert.True(invocationStarted.Wait(Timeout));
                Assert.False(invocation.IsCompleted);
                disposeSemaphore.Set();

                var waiter = await invocation.WaitAsync(Timeout);
                await Assert.ThrowsAsync<ObjectDisposedException>(
                    async () => await waiter.WaitAsync(Timeout));
                Assert.True(waiter.IsFaulted);
            }
            finally
            {
                disposeSemaphore.Set();
                await stateLockHolder.WaitAsync(Timeout);
                semaphore.Dispose();
            }

            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task CancellationThenGrantRace_PreservesCountForWinningOutcome()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellation = new CancellationTokenSource();
            var waiter = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                cancellation.Token);

            Assert.Equal(1, semaphore.QueuedWaiterCount);

            lock (GetStateLock(semaphore))
            {
                // Cancellation completes the timeout side of Task.WhenAny first. Release then
                // grants the still-queued request before its cancellation continuation can take
                // the state lock. Cleanup must return that granted count exactly once.
                cancellation.Cancel();
                semaphore.Release(1);
            }

            var acquired = false;
            var exception = await Record.ExceptionAsync(async () =>
            {
                acquired = await waiter.WaitAsync(Timeout);
            });
            if (exception == null)
            {
                Assert.True(acquired);
                Assert.Equal(0, semaphore.CurrentCount);
                semaphore.Release(1);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
            }

            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task GrantThenCancellationRace_PreservesCountForWinningOutcome()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellation = new CancellationTokenSource();
            var waiter = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                cancellation.Token);

            Assert.Equal(1, semaphore.QueuedWaiterCount);

            lock (GetStateLock(semaphore))
            {
                semaphore.Release(1);
                cancellation.Cancel();
            }

            var acquired = false;
            var exception = await Record.ExceptionAsync(async () =>
            {
                acquired = await waiter.WaitAsync(Timeout);
            });
            if (exception == null)
            {
                Assert.True(acquired);
                Assert.Equal(0, semaphore.CurrentCount);
                semaphore.Release(1);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
            }

            Assert.Equal(0, semaphore.QueuedWaiterCount);
            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
        }

        [Fact]
        public async Task CancellationAndReleaseAllRace_PreservesResetCount()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellation = new CancellationTokenSource();
            var leaseTask = semaphore.WaitAndReleaseAsync(1, cancellation.Token);

            Assert.Equal(1, semaphore.QueuedWaiterCount);

            lock (GetStateLock(semaphore))
            {
                cancellation.Cancel();
                semaphore.ReleaseAll();
            }

            IDisposable lease = null;
            var exception = await Record.ExceptionAsync(async () =>
            {
                lease = await leaseTask.WaitAsync(Timeout);
            });
            if (exception == null)
            {
                Assert.NotNull(lease);
                lease.Dispose();
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
            }

            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
            Assert.Equal(0, semaphore.QueuedWaiterCount);
        }

        [Fact]
        public async Task ReleaseAndCancellationRace_StressPreservesExactlyOnceCompletionAndCount()
        {
            await RunReleaseAndCancellationStressAsync().WaitAsync(Timeout);
        }

        [Fact]
        public async Task ReleaseAndTimeoutRace_StressPreservesExactlyOnceCompletionAndCount()
        {
            await RunReleaseAndTimeoutStressAsync().WaitAsync(Timeout);
        }

        [Fact]
        public async Task ReleaseAndDisposeRace_StressCompletesWaiterExactlyOnceAndEmptiesQueue()
        {
            await RunReleaseAndDisposeStressAsync().WaitAsync(Timeout);
        }

        [Fact]
        public async Task CancellationAndReleaseAllRace_StressPreservesExactlyOnceCompletionAndResetCount()
        {
            await RunCancellationAndReleaseAllStressAsync().WaitAsync(Timeout);
        }

        private static async Task RunReleaseAndCancellationStressAsync()
        {
            for (var iteration = 0; iteration < StressIterations; ++iteration)
            {
                using var semaphore = new AsyncSemaphore(0, 1);
                using var cancellation = new CancellationTokenSource();
                var waiter = semaphore.WaitAsync(
                    1,
                    AsyncSemaphore.InfiniteTimeSpan,
                    AsyncSemaphore.DefaultPriority,
                    cancellation.Token);
                var completionCount = 0;
                var completionObserver = ObserveCompletionOnce(waiter, () =>
                    Interlocked.Increment(ref completionCount));

                Assert.Equal(1, semaphore.QueuedWaiterCount);

                lock (GetStateLock(semaphore))
                {
                    if ((iteration & 1) == 0)
                    {
                        cancellation.Cancel();
                        semaphore.Release();
                    }
                    else
                    {
                        semaphore.Release();
                        cancellation.Cancel();
                    }
                }

                var acquired = false;
                var exception = await Record.ExceptionAsync(async () =>
                {
                    acquired = await waiter;
                });
                if (exception == null)
                {
                    Assert.True(acquired);
                    semaphore.Release();
                }
                else
                {
                    Assert.IsAssignableFrom<OperationCanceledException>(exception);
                }

                cancellation.Cancel();
                await completionObserver;
                Assert.Equal(1, Volatile.Read(ref completionCount));
                Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
                Assert.Equal(0, semaphore.QueuedWaiterCount);
            }
        }

        private static async Task RunReleaseAndTimeoutStressAsync()
        {
            for (var iteration = 0; iteration < StressIterations; ++iteration)
            {
                using var semaphore = new AsyncSemaphore(0, 1);
                var waiter = semaphore.WaitAsync(1, RaceTimeout);
                var completionCount = 0;
                var completionObserver = ObserveCompletionOnce(waiter, () =>
                    Interlocked.Increment(ref completionCount));

                Assert.Equal(1, semaphore.QueuedWaiterCount);

                if ((iteration & 1) == 0)
                {
                    semaphore.Release();
                }
                else
                {
                    using var releaseGate = new ManualResetEventSlim(false);
                    var stateLockEntered = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseTask = ReleaseWhileHoldingStateLock(
                        semaphore,
                        stateLockEntered,
                        releaseGate);

                    await stateLockEntered.Task;
                    await Task.Delay(RaceTimeout + TimeSpan.FromMilliseconds(10));
                    releaseGate.Set();
                    await releaseTask;
                }

                var acquired = await waiter;
                if (acquired)
                {
                    semaphore.Release();
                }

                await completionObserver;
                Assert.Equal(1, Volatile.Read(ref completionCount));
                Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
                Assert.Equal(0, semaphore.QueuedWaiterCount);
            }
        }

        private static async Task RunReleaseAndDisposeStressAsync()
        {
            for (var iteration = 0; iteration < StressIterations; ++iteration)
            {
                var semaphore = new AsyncSemaphore(0, 1);
                var waiter = semaphore.WaitAsync();
                var completionCount = 0;
                var completionObserver = ObserveCompletionOnce(waiter, () =>
                    Interlocked.Increment(ref completionCount));
                using var start = new ManualResetEventSlim(false);

                Assert.Equal(1, semaphore.QueuedWaiterCount);

                var releaseTask = Task.Run(() =>
                {
                    Assert.True(start.Wait(Timeout));
                    return Record.Exception(semaphore.Release);
                });
                var disposeTask = Task.Run(() =>
                {
                    Assert.True(start.Wait(Timeout));
                    semaphore.Dispose();
                });

                start.Set();
                await Task.WhenAll(releaseTask, disposeTask);

                var waiterException = await Record.ExceptionAsync(async () => await waiter);
                var releaseException = await releaseTask;
                if (releaseException == null)
                {
                    Assert.Null(waiterException);
                }
                else
                {
                    Assert.IsType<ObjectDisposedException>(releaseException);
                    Assert.IsType<ObjectDisposedException>(waiterException);
                }

                await completionObserver;
                semaphore.Dispose();
                Assert.Equal(1, Volatile.Read(ref completionCount));
                Assert.Equal(0, semaphore.CurrentCount);
                Assert.Equal(0, semaphore.QueuedWaiterCount);
                Assert.Throws<ObjectDisposedException>(semaphore.ReleaseAll);
            }
        }

        private static async Task RunCancellationAndReleaseAllStressAsync()
        {
            const int waiterCount = 8;

            for (var iteration = 0; iteration < StressIterations; ++iteration)
            {
                using var semaphore = new AsyncSemaphore(0, 1);
                using var start = new ManualResetEventSlim(false);
                var cancellations = new CancellationTokenSource[waiterCount];
                var waiters = new Task<IDisposable>[waiterCount];
                var completionCounts = new int[waiterCount];
                var completionObservers = new Task[waiterCount];

                for (var waiterIndex = 0; waiterIndex < waiterCount; ++waiterIndex)
                {
                    cancellations[waiterIndex] = new CancellationTokenSource();
                    waiters[waiterIndex] = semaphore.WaitAndReleaseAsync(
                        1,
                        cancellations[waiterIndex].Token);
                    var observedIndex = waiterIndex;
                    completionObservers[waiterIndex] = ObserveCompletionOnce(
                        waiters[waiterIndex],
                        () => Interlocked.Increment(ref completionCounts[observedIndex]));
                }

                Assert.Equal(waiterCount, semaphore.QueuedWaiterCount);

                var cancelTask = Task.Run(() =>
                {
                    Assert.True(start.Wait(Timeout));
                    foreach (var cancellation in cancellations)
                    {
                        cancellation.Cancel();
                    }
                });
                var releaseAllTask = Task.Run(() =>
                {
                    Assert.True(start.Wait(Timeout));
                    semaphore.ReleaseAll();
                });

                start.Set();
                await Task.WhenAll(cancelTask, releaseAllTask);

                foreach (var waiter in waiters)
                {
                    IDisposable lease = null;
                    var exception = await Record.ExceptionAsync(async () =>
                    {
                        lease = await waiter;
                    });
                    if (exception == null)
                    {
                        Assert.NotNull(lease);
                        lease.Dispose();
                        lease.Dispose();
                    }
                    else
                    {
                        Assert.IsAssignableFrom<OperationCanceledException>(exception);
                    }
                }

                await Task.WhenAll(completionObservers);
                foreach (var completionCount in completionCounts)
                {
                    Assert.Equal(1, completionCount);
                }

                foreach (var cancellation in cancellations)
                {
                    cancellation.Dispose();
                }

                Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
                Assert.Equal(0, semaphore.QueuedWaiterCount);
            }
        }

        private static Task ObserveCompletionOnce(Task task, Action onCompletion)
        {
            return task.ContinueWith(
                _ => onCompletion(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static Task ReleaseWhileHoldingStateLock(
            AsyncSemaphore semaphore,
            TaskCompletionSource<bool> stateLockEntered,
            ManualResetEventSlim releaseGate)
        {
            var stateLock = GetStateLock(semaphore);
            return Task.Run(() =>
            {
                lock (stateLock)
                {
                    stateLockEntered.TrySetResult(true);
                    Assert.True(releaseGate.Wait(Timeout));
                    semaphore.Release();
                }
            });
        }

        private static object GetStateLock(AsyncSemaphore semaphore)
        {
            var stateLock = typeof(AsyncSemaphore)
                .GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(semaphore);
            Assert.NotNull(stateLock);
            return stateLock;
        }

        private static Task DisposeWhileHoldingStateLock(
            AsyncSemaphore semaphore,
            TaskCompletionSource<bool> stateLockEntered,
            ManualResetEventSlim disposeSemaphore)
        {
            var stateLock = GetStateLock(semaphore);
            return Task.Run(() =>
            {
                lock (stateLock)
                {
                    stateLockEntered.TrySetResult(true);
                    Assert.True(disposeSemaphore.Wait(Timeout));
                    semaphore.Dispose();
                }
            });
        }

        private static Task HoldStateLock(
            AsyncSemaphore semaphore,
            TaskCompletionSource<bool> stateLockEntered,
            ManualResetEventSlim releaseStateLock)
        {
            var stateLock = GetStateLock(semaphore);
            return Task.Run(() =>
            {
                lock (stateLock)
                {
                    stateLockEntered.TrySetResult(true);
                    Assert.True(releaseStateLock.Wait(Timeout));
                }
            });
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.WaitAsync(Timeout);
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
            }
        }
    }
}
