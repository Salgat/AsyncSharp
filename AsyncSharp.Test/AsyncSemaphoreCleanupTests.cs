using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Coverage for waiter-queue cleanup on cancel/timeout, dispose semantics, argument validation, count-0 waiters,
    /// and ReleaseAll over-grant behavior — paths the original suite did not exercise.
    /// </summary>
    public class AsyncSemaphoreCleanupTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task WaitAsync_Timeout_RemovesWaiterAndEmptiesPriorityBucket()
        {
            using var semaphore = new AsyncSemaphore(0, 1);

            var acquired = await semaphore.WaitAsync(1, TimeSpan.FromMilliseconds(50));

            Assert.False(acquired);
            Assert.Empty(semaphore._queuedAcquireRequests);
            Assert.Empty(semaphore._activePriorities);
        }

        [Fact]
        public async Task WaitAsync_Timeout_NonDefaultPriority_RemovesEmptyBucket()
        {
            using var semaphore = new AsyncSemaphore(0, 1);

            var acquired = await semaphore.WaitAsync(1, TimeSpan.FromMilliseconds(50), priority: 5, CancellationToken.None);

            Assert.False(acquired);
            Assert.Empty(semaphore._queuedAcquireRequests);
            Assert.Empty(semaphore._activePriorities);
        }

        [Fact]
        public void Wait_Cancelled_RemovesWaiterAndEmptiesPriorityBucket()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellationTokenSource = new CancellationTokenSource(50);

            Assert.Throws<OperationCanceledException>(()
                => semaphore.Wait(1, AsyncSemaphore.InfiniteTimeSpan, cancellationTokenSource.Token));

            Assert.Empty(semaphore._queuedAcquireRequests);
            Assert.Empty(semaphore._activePriorities);
        }

        [Fact]
        public async Task WaitAsync_Cancelled_RemovesWaiterAndEmptiesPriorityBucket()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellationTokenSource = new CancellationTokenSource(50);

            await Assert.ThrowsAsync<OperationCanceledException>(()
                => semaphore.WaitAsync(1, AsyncSemaphore.InfiniteTimeSpan, AsyncSemaphore.DefaultPriority, cancellationTokenSource.Token));

            Assert.Empty(semaphore._queuedAcquireRequests);
            Assert.Empty(semaphore._activePriorities);
        }

        [Fact]
        public void Wait_ZeroCount_GrantedImmediatelyEvenWithNoCountAvailable()
        {
            using var semaphore = new AsyncSemaphore(0, 1);

            // Acquiring zero is always satisfiable; it must not block until a later positive Release.
            Assert.True(semaphore.Wait(0, Timeout));
            Assert.Empty(semaphore._queuedAcquireRequests);
        }

        [Fact]
        public async Task WaitAsync_ZeroCount_CompletesImmediatelyEvenWithNoCountAvailable()
        {
            using var semaphore = new AsyncSemaphore(0, 1);

            var acquired = await semaphore.WaitAsync(0, Timeout);

            Assert.True(acquired);
            Assert.Empty(semaphore._queuedAcquireRequests);
        }

        [Fact]
        public async Task ReleaseAll_GrantsAllWaiters_EvenWhenTheirSumExceedsMaxCount()
        {
            using var semaphore = new AsyncSemaphore(0, 1);

            var w1 = semaphore.WaitAsync(1, Timeout);
            var w2 = semaphore.WaitAsync(1, Timeout);
            var w3 = semaphore.WaitAsync(1, Timeout);

            semaphore.ReleaseAll();

            var results = await Task.WhenAll(w1, w2, w3);
            Assert.All(results, result => Assert.True(result));
            Assert.Equal(1, semaphore.CurrentCount); // reset to MaxCount
            Assert.Empty(semaphore._queuedAcquireRequests);
        }

        [Fact]
        public void Wait_CountExceedingMax_ThrowsWithCorrectParamName()
        {
            using var semaphore = new AsyncSemaphore(1, 1);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => semaphore.Wait(2));
            Assert.Equal("count", ex.ParamName);
        }

        [Fact]
        public void Wait_NegativeCount_ThrowsWithCorrectParamName()
        {
            using var semaphore = new AsyncSemaphore(1, 1);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => semaphore.Wait(-1));
            Assert.Equal("count", ex.ParamName);
        }

        [Fact]
        public void Release_ExceedingMax_ThrowsWithCorrectParamName()
        {
            using var semaphore = new AsyncSemaphore(1, 1);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => semaphore.Release(1)); // would make CurrentCount 2 > max 1
            Assert.Equal("count", ex.ParamName);
        }

        [Fact]
        public void Constructor_StartingExceedsMax_ThrowsWithCorrectParamName()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncSemaphore(2, 1));
            Assert.Equal("startingCount", ex.ParamName);
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var semaphore = new AsyncSemaphore(1, 1);
            semaphore.Dispose();
            semaphore.Dispose(); // must not throw
        }

        [Fact]
        public async Task UseAfterDispose_ThrowsObjectDisposedException_NotArgumentNull()
        {
            var semaphore = new AsyncSemaphore(1, 1);
            semaphore.Dispose();

            Assert.Throws<ObjectDisposedException>(() => semaphore.Wait());
            Assert.Throws<ObjectDisposedException>(() => semaphore.Release());
            Assert.Throws<ObjectDisposedException>(() => semaphore.AcquireUpTo(1));
            Assert.Throws<ObjectDisposedException>(() => semaphore.ReleaseUpTo(1));
            Assert.Throws<ObjectDisposedException>(() => semaphore.ReleaseAll());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => semaphore.WaitAsync());
        }
    }
}
