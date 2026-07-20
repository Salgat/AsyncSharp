using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Coverage for the non-default <see cref="AsyncSemaphore.WaiterPriority"/> modes, which were previously untested.
    /// Waiters are enqueued synchronously by WaitAsync before its first await, so calling WaitAsync without awaiting
    /// gives a deterministic enqueue order for these ordering assertions.
    /// </summary>
    public class AsyncSemaphorePriorityTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task LowToHigh_GrantsLowestCountFirst_EvenWhenEnqueuedLater()
        {
            using var semaphore = new AsyncSemaphore(0, 10, AsyncSemaphore.WaiterPriority.LowToHigh);

            var big = semaphore.WaitAsync(3, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);   // enqueued first
            var small = semaphore.WaitAsync(1, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None); // enqueued second

            semaphore.Release(1);

            var finished = await Task.WhenAny(big, small);
            var bigWasPending = !big.IsCompleted;

            semaphore.Release(3);
            var results = await Task.WhenAll(big, small);

            Assert.Same(small, finished);
            Assert.True(bigWasPending);
            Assert.All(results, result => Assert.True(result));
        }

        [Fact]
        public async Task HighToLow_GrantsHighestCountFirst_EvenWhenEnqueuedLater()
        {
            using var semaphore = new AsyncSemaphore(0, 10, AsyncSemaphore.WaiterPriority.HighToLow);

            var small = semaphore.WaitAsync(1, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None); // enqueued first
            var big = semaphore.WaitAsync(3, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);   // enqueued second

            semaphore.Release(3);

            var finished = await Task.WhenAny(big, small);
            var smallWasPending = !small.IsCompleted;

            semaphore.Release(1);
            var results = await Task.WhenAll(big, small);

            Assert.Same(big, finished);
            Assert.True(smallWasPending);
            Assert.All(results, result => Assert.True(result));
        }

        [Fact]
        public async Task FirstInFirstOut_Fair_DoesNotSkipBlockedHead()
        {
            using var semaphore = new AsyncSemaphore(0, 10, AsyncSemaphore.WaiterPriority.FirstInFirstOut);

            var big = semaphore.WaitAsync(3, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);   // head
            var small = semaphore.WaitAsync(1, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);

            // Only 1 available: the fair head (count 3) is not satisfiable, and fairness forbids skipping to the
            // smaller later waiter, so nobody is granted.
            semaphore.Release(1);
            await Task.Delay(100);
            Assert.False(big.IsCompleted);
            Assert.False(small.IsCompleted);

            // Now enough for the head; order is preserved (head first, then the next).
            semaphore.Release(2);
            Assert.True(await big);
            semaphore.Release(1);
            Assert.True(await small);
        }

        [Theory]
        [InlineData(AsyncSemaphore.WaiterPriority.FirstInFirstOutUnfair)]
        [InlineData(AsyncSemaphore.WaiterPriority.Unfair)]
        public async Task Unfair_SkipsBlockedHeadToSatisfyLaterWaiter(AsyncSemaphore.WaiterPriority priority)
        {
            using var semaphore = new AsyncSemaphore(0, 10, priority);

            var big = semaphore.WaitAsync(3, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);   // head, unsatisfiable
            var small = semaphore.WaitAsync(1, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);

            // Only 1 available: unfair modes skip the blocked head and satisfy the later, smaller waiter.
            semaphore.Release(1);

            var finished = await Task.WhenAny(big, small);
            var bigWasPending = !big.IsCompleted;

            semaphore.Release(3);
            var results = await Task.WhenAll(big, small);

            Assert.Same(small, finished);
            Assert.True(bigWasPending);
            Assert.All(results, result => Assert.True(result));
        }

        [Fact]
        public async Task ExplicitPriority_HigherIntegerPriorityWins_AcrossPriorityBuckets()
        {
            using var semaphore = new AsyncSemaphore(0, 1, AsyncSemaphore.WaiterPriority.FirstInFirstOut);

            var normal = semaphore.WaitAsync(1, Timeout, AsyncSemaphore.DefaultPriority, CancellationToken.None);
            var high = semaphore.WaitAsync(1, Timeout, AsyncSemaphore.DefaultPriority + 1, CancellationToken.None);

            semaphore.Release(1);

            var finished = await Task.WhenAny(normal, high);
            var normalWasPending = !normal.IsCompleted;
            semaphore.Release(1);
            var results = await Task.WhenAll(normal, high);

            Assert.Same(high, finished);
            Assert.True(normalWasPending);
            Assert.All(results, result => Assert.True(result));
        }

        [Fact]
        public async Task ExplicitPriority_BlockedHigherBucket_PreventsLowerBucketOvertake()
        {
            using var semaphore = new AsyncSemaphore(
                0,
                2,
                AsyncSemaphore.WaiterPriority.FirstInFirstOutUnfair);

            var high = semaphore.WaitAsync(
                2,
                Timeout,
                AsyncSemaphore.DefaultPriority + 1,
                CancellationToken.None);
            var normal = semaphore.WaitAsync(
                1,
                Timeout,
                AsyncSemaphore.DefaultPriority,
                CancellationToken.None);

            semaphore.Release(1);
            await Task.Delay(100);

            var highWasPending = !high.IsCompleted;
            var normalWasPending = !normal.IsCompleted;

            // Settle both tasks before asserting so a failure never abandons a waiter.
            semaphore.ReleaseUpTo(2);
            var highResult = await high;
            semaphore.ReleaseUpTo(1);
            var normalResult = await normal;

            Assert.True(highWasPending);
            Assert.True(normalWasPending);
            Assert.True(highResult);
            Assert.True(normalResult);
        }
    }
}
