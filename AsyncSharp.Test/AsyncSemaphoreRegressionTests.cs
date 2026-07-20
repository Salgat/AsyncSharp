using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Regression coverage for known semaphore correctness and lifecycle defects.
    /// Every blocked operation has bounded cleanup so a regression fails instead of hanging the suite.
    /// </summary>
    public class AsyncSemaphoreRegressionTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public void Release_WhenAdditionOverflows_ThrowsWithoutChangingCount()
        {
            using var semaphore = new AsyncSemaphore(1, int.MaxValue);

            var exception = Record.Exception(() => semaphore.Release(int.MaxValue));
            var currentCount = semaphore.CurrentCount;

            Assert.Equal(1, currentCount);
            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("count", outOfRange.ParamName);
        }

        [Fact]
        public void ReleaseUpTo_WhenAdditionOverflows_ClampsToRemainingCapacity()
        {
            using var semaphore = new AsyncSemaphore(1, int.MaxValue);

            var released = semaphore.ReleaseUpTo(int.MaxValue);

            Assert.Equal(int.MaxValue - 1, released);
            Assert.Equal(int.MaxValue, semaphore.CurrentCount);
        }

        [Fact]
        public async Task Dispose_FaultsPendingAsynchronousWaiter()
        {
            var semaphore = new AsyncSemaphore(0, 1);
            using var cleanupCancellation = new CancellationTokenSource();

            var waiter = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                cleanupCancellation.Token);

            Exception waiterException;
            try
            {
                semaphore.Dispose();
                waiterException = await Record.ExceptionAsync(
                    async () => { await waiter.WaitAsync(Timeout); });
            }
            finally
            {
                // Cancellation is a cleanup fallback if disposal regresses.
                cleanupCancellation.Cancel();
                await ObserveAsync(waiter);
                semaphore.Dispose();
            }

            Assert.IsType<ObjectDisposedException>(waiterException);
        }

        [Fact]
        public async Task Dispose_FaultsPendingSynchronousWaiter()
        {
            var semaphore = new AsyncSemaphore(0, 1);
            using var cleanupCancellation = new CancellationTokenSource();
            using var queuedTimeout = new CancellationTokenSource(Timeout);

            var waiter = Task.Run(() => Record.Exception(() =>
                semaphore.Wait(cleanupCancellation.Token)));

            try
            {
                await WaitUntilAsync(
                    () => semaphore.QueuedWaiterCount == 1,
                    queuedTimeout.Token);

                semaphore.Dispose();
                var waiterException = await waiter.WaitAsync(Timeout);

                Assert.IsType<ObjectDisposedException>(waiterException);
            }
            finally
            {
                cleanupCancellation.Cancel();
                await ObserveAsync(waiter);
                semaphore.Dispose();
            }
        }

        [Fact]
        public async Task WaitAsync_WhenDelayRejectsTimeout_DoesNotConsumeAvailableCount()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            var acquired = false;

            var exception = await Record.ExceptionAsync(async () =>
            {
                acquired = await semaphore.WaitAsync(1, TimeSpan.MaxValue);
            });

            var currentCountAfterFailure = semaphore.CurrentCount;
            if (currentCountAfterFailure == 0)
            {
                // Restore the count after observing the regression so cleanup is balanced.
                semaphore.Release(1);
            }

            Assert.False(acquired);
            Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal(1, currentCountAfterFailure);
        }

        [Theory]
        [InlineData(AsyncSemaphore.WaiterPriority.FirstInFirstOutUnfair)]
        [InlineData(AsyncSemaphore.WaiterPriority.Unfair)]
        public void Wait_WhenTimeoutExceedsSupportedRange_DoesNotConsumeAvailableCount(
            AsyncSemaphore.WaiterPriority waiterPriority)
        {
            using var semaphore = new AsyncSemaphore(1, 1, waiterPriority);

            var exception = Record.Exception(() =>
                semaphore.Wait(1, TimeSpan.MaxValue));

            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("timeout", outOfRange.ParamName);
            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Empty(semaphore._queuedAcquireRequests);
        }

        [Fact]
        public async Task Wait_TimeoutIncludesInternalLockContention()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var releaseLock = new ManualResetEventSlim(false);
            var lockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var lockHolder = HoldInternalLock(semaphore, lockEntered, releaseLock);

            await lockEntered.Task.WaitAsync(Timeout);
            var waiter = Task.Run(() =>
                semaphore.Wait(1, TimeSpan.FromMilliseconds(30)));
            try
            {
                var completed = await Task.WhenAny(waiter, Task.Delay(Timeout));

                Assert.Same(waiter, completed);
                Assert.False(await waiter);
                Assert.Equal(1, semaphore.CurrentCount);
            }
            finally
            {
                releaseLock.Set();
                await lockHolder.WaitAsync(CleanupTimeout);
            }
        }

        [Fact]
        public async Task WaitAsync_TimeoutIncludesInternalLockContention()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var releaseLock = new ManualResetEventSlim(false);
            var lockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var lockHolder = HoldInternalLock(semaphore, lockEntered, releaseLock);

            await lockEntered.Task.WaitAsync(Timeout);
            var waiter = semaphore.WaitAsync(
                1,
                TimeSpan.FromMilliseconds(30));
            try
            {
                var completed = await Task.WhenAny(waiter, Task.Delay(Timeout));

                Assert.Same(waiter, completed);
                Assert.False(await waiter);
                Assert.Equal(1, semaphore.CurrentCount);
            }
            finally
            {
                releaseLock.Set();
                await lockHolder.WaitAsync(CleanupTimeout);
            }
        }

        [Fact]
        public async Task Wait_CancellationInterruptsInternalLockContention()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var releaseLock = new ManualResetEventSlim(false);
            using var cancellation = new CancellationTokenSource();
            var lockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var waitStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var lockHolder = HoldInternalLock(semaphore, lockEntered, releaseLock);

            await lockEntered.Task.WaitAsync(Timeout);
            var waiter = Task.Run(() =>
            {
                waitStarted.TrySetResult(true);
                return Record.Exception(() => semaphore.Wait(cancellation.Token));
            });
            try
            {
                await waitStarted.Task.WaitAsync(Timeout);
                await Task.Delay(50);
                cancellation.Cancel();
                var completed = await Task.WhenAny(waiter, Task.Delay(Timeout));

                Assert.Same(waiter, completed);
                Assert.IsAssignableFrom<OperationCanceledException>(await waiter);
                Assert.Equal(1, semaphore.CurrentCount);
            }
            finally
            {
                cancellation.Cancel();
                releaseLock.Set();
                await ObserveAsync(waiter);
                await lockHolder.WaitAsync(CleanupTimeout);
            }
        }

        [Fact]
        public async Task WaitAsync_CancellationInterruptsInternalLockContentionWithoutBlockingCaller()
        {
            using var semaphore = new AsyncSemaphore(1, 1);
            using var releaseLock = new ManualResetEventSlim(false);
            using var cancellation = new CancellationTokenSource();
            var lockEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var lockHolder = HoldInternalLock(semaphore, lockEntered, releaseLock);

            await lockEntered.Task.WaitAsync(Timeout);
            var invocation = Task.Factory.StartNew(
                () => semaphore.WaitAsync(cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            Task waiter = null;
            try
            {
                var invocationCompleted = await Task.WhenAny(
                    invocation,
                    Task.Delay(Timeout));
                Assert.Same(invocation, invocationCompleted);
                waiter = await invocation;

                cancellation.Cancel();
                var exception = await Record.ExceptionAsync(async () =>
                    await waiter.WaitAsync(Timeout));

                Assert.IsAssignableFrom<OperationCanceledException>(exception);
                Assert.Equal(1, semaphore.CurrentCount);
            }
            finally
            {
                cancellation.Cancel();
                releaseLock.Set();
                if (waiter != null)
                {
                    await ObserveAsync(waiter);
                }
                else
                {
                    try
                    {
                        waiter = await invocation.WaitAsync(CleanupTimeout);
                        await ObserveAsync(waiter);
                    }
                    catch (Exception exception) when (exception is not TimeoutException)
                    {
                    }
                }
                await lockHolder.WaitAsync(CleanupTimeout);
            }
        }

        [Fact]
        public async Task ReleaseAll_GrantedWaitAndReleaseLease_CanStillBeDisposed()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var leaseCancellation = new CancellationTokenSource(Timeout);
            var leaseTask = semaphore.WaitAndReleaseAsync(leaseCancellation.Token);

            semaphore.ReleaseAll();
            var lease = await leaseTask;

            var exception = Record.Exception(lease.Dispose);

            Assert.Null(exception);
            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);
        }

        [Fact]
        public void ReleaseAll_StaleLease_CanBeDisposedAfterSemaphore()
        {
            var semaphore = new AsyncSemaphore(1, 1);
            var staleLease = semaphore.WaitAndRelease();

            semaphore.ReleaseAll();
            semaphore.Dispose();
            var exception = Record.Exception(staleLease.Dispose);

            Assert.Null(exception);
        }

        [Fact]
        public async Task CancellingBlockedFairHead_PromotesSatisfiableFollower()
        {
            using var semaphore = new AsyncSemaphore(
                0,
                2,
                AsyncSemaphore.WaiterPriority.FirstInFirstOut);
            using var headCancellation = new CancellationTokenSource();
            using var followerCancellation =
                new CancellationTokenSource(CleanupTimeout);

            var head = semaphore.WaitAsync(
                2,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                headCancellation.Token);
            var follower = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                followerCancellation.Token);

            try
            {
                semaphore.Release(1);
                headCancellation.Cancel();
                var headException = await Record.ExceptionAsync(
                    async () => { await head; });

                var completed = await Task.WhenAny(follower, Task.Delay(Timeout));
                var promotedWithoutAnotherRelease = completed == follower;
                if (!promotedWithoutAnotherRelease)
                {
                    // Trigger one final drain so cleanup can finish after a regression.
                    semaphore.Release(0);
                }

                Assert.True(await follower);
                semaphore.Release(1);

                Assert.IsAssignableFrom<OperationCanceledException>(headException);
                Assert.True(promotedWithoutAnotherRelease);
            }
            finally
            {
                headCancellation.Cancel();
                followerCancellation.Cancel();
                await ObserveAsync(head);
                await ObserveAsync(follower);
            }
        }

        [Theory]
        [InlineData(AsyncSemaphore.WaiterPriority.FirstInFirstOutUnfair)]
        [InlineData(AsyncSemaphore.WaiterPriority.Unfair)]
        public async Task UnfairModes_ZeroCountWaiter_IsNotBlockedByPositiveHead(
            AsyncSemaphore.WaiterPriority waiterPriority)
        {
            using var semaphore = new AsyncSemaphore(0, 1, waiterPriority);
            using var headCancellation = new CancellationTokenSource();
            using var zeroCancellation = new CancellationTokenSource();

            var blockedHead = semaphore.WaitAsync(
                1,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                headCancellation.Token);
            var zeroCountFollower = semaphore.WaitAsync(
                0,
                AsyncSemaphore.InfiniteTimeSpan,
                AsyncSemaphore.DefaultPriority,
                zeroCancellation.Token);

            var completed = await Task.WhenAny(zeroCountFollower, Task.Delay(Timeout));
            var completedWhileHeadWasBlocked = completed == zeroCountFollower;

            headCancellation.Cancel();
            zeroCancellation.Cancel();
            await ObserveAsync(blockedHead);
            await ObserveAsync(zeroCountFollower);

            Assert.True(completedWhileHeadWasBlocked);
            Assert.True(zeroCountFollower.IsCompletedSuccessfully);
        }

        [Fact]
        public void Constructor_InvalidWaiterPriority_ThrowsArgumentOutOfRangeException()
        {
            AsyncSemaphore semaphore = null;

            var exception = Record.Exception(() => semaphore = new AsyncSemaphore(
                0,
                1,
                (AsyncSemaphore.WaiterPriority)int.MaxValue));
            semaphore?.Dispose();

            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("waiterPriority", outOfRange.ParamName);
        }

        [Fact]
        public void WaitAndReleaseLease_Dispose_IsIdempotent()
        {
            // MaxCount 2 exposes the second release as silent over-release rather than an exception.
            using var semaphore = new AsyncSemaphore(1, 2);
            var lease = semaphore.WaitAndRelease();

            lease.Dispose();
            var secondDisposeException = Record.Exception(lease.Dispose);

            Assert.Null(secondDisposeException);
            Assert.Equal(1, semaphore.CurrentCount);
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.WaitAsync(CleanupTimeout);
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                // Cleanup only: ensure canceled or faulted tasks are observed.
            }
        }

        private static Task HoldInternalLock(
            AsyncSemaphore semaphore,
            TaskCompletionSource<bool> lockEntered,
            ManualResetEventSlim releaseLock)
        {
            var stateLock = typeof(AsyncSemaphore)
                .GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(semaphore);
            Assert.NotNull(stateLock);

            return Task.Run(() =>
            {
                lock (stateLock)
                {
                    lockEntered.TrySetResult(true);
                    Assert.True(releaseLock.Wait(CleanupTimeout));
                }
            });
        }

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            CancellationToken cancellationToken)
        {
            while (!condition())
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }
}
