using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    public class AsyncSemaphoreTests
    {
        [Fact]
        public void Wait()
        {
            using var semaphore = new AsyncSemaphore(1, 1, true);

            semaphore.Wait();
            var acquiredLock = semaphore.Wait(1, TimeSpan.FromMilliseconds(0));
            
            Assert.False(acquiredLock);
        }

        [Fact]
        public async Task WaitAsync()
        {
            using var semaphore = new AsyncSemaphore(1, 1, true);

            await semaphore.WaitAsync();
            var acquiredLock = await semaphore.WaitAsync(1, TimeSpan.FromMilliseconds(0));

            Assert.False(acquiredLock);
        }

        [Fact]
        public void Wait_Release()
        {
            using var semaphore = new AsyncSemaphore(1, 1, true);

            for (var i = 0; i < 1000; ++i)
            {
                semaphore.Wait();
                semaphore.Release();
            }
        }

        [Fact]
        public async Task WaitAsync_Release()
        {
            using var semaphore = new AsyncSemaphore(1, 1, true);

            for (var i = 0; i < 1000; ++i)
            {
                await semaphore.WaitAsync();
                semaphore.Release();
            }
        }

        [Fact]
        public void Wait_Random()
        {
            using var semaphore = new AsyncSemaphore(1000, 1000, true);

            var random = new Random(1234);
            var amountLeft = 1000;
            while (amountLeft > 0)
            {
                var maxToAcquire = amountLeft / 2;
                var amountToAcquire = random.Next(1, maxToAcquire > 1 ? maxToAcquire : 1);
                Assert.True(semaphore.Wait(amountToAcquire, TimeSpan.FromMilliseconds(0)));
                amountLeft -= amountToAcquire;
            }
            semaphore.Release(1000);
            Assert.True(semaphore.Wait(1000, TimeSpan.FromMilliseconds(0)));
        }

        [Fact]
        public async Task WaitAsync_Random()
        {
            using var semaphore = new AsyncSemaphore(1000, 1000, true);

            var random = new Random(1234);
            var amountLeft = 1000;
            while (amountLeft > 0)
            {
                var maxToAcquire = amountLeft / 2;
                var amountToAcquire = random.Next(1, maxToAcquire > 1 ? maxToAcquire : 1);
                Assert.True(await semaphore.WaitAsync(amountToAcquire, TimeSpan.FromMilliseconds(0)));
                amountLeft -= amountToAcquire;
            }
            semaphore.Release(1000);
            Assert.True(await semaphore.WaitAsync(1000, TimeSpan.FromMilliseconds(0)));
        }

        [Fact]
        public void ReleaseAll()
        {
            using var semaphore = new AsyncSemaphore(0, 10, true);

            var acquiredWhenNoneAvailable = semaphore.Wait(10, TimeSpan.FromMilliseconds(0));
            semaphore.ReleaseAll();
            var acquiredWhenAllAvailable = semaphore.Wait(10, TimeSpan.FromMilliseconds(0));

            Assert.False(acquiredWhenNoneAvailable);
            Assert.True(acquiredWhenAllAvailable);
        }

        [Fact]
        public void Release()
        {
            using var semaphore = new AsyncSemaphore(0, 5, true);

            var acquiredWhenNoneAvailable = semaphore.Wait(5, TimeSpan.FromMilliseconds(0));
            semaphore.Release(5);
            var acquiredWhenAllAvailable = semaphore.Wait(5, TimeSpan.FromMilliseconds(0));

            Assert.False(acquiredWhenNoneAvailable);
            Assert.True(acquiredWhenAllAvailable);
        }

        [Fact]
        public void CurrentCount_MaxCount()
        {
            using var semaphore = new AsyncSemaphore(14, 18, true);
            Assert.Equal(18, semaphore.MaxCount);
            Assert.Equal(14, semaphore.CurrentCount);

            semaphore.Release(2);
            Assert.Equal(16, semaphore.CurrentCount);

            semaphore.Release();
            semaphore.Release();
            Assert.Equal(semaphore.MaxCount, semaphore.CurrentCount);

            semaphore.Wait(18);
            Assert.Equal(0, semaphore.CurrentCount);
        }

        [Fact]
        public void WaitAndRelease()
        {
            using var semaphore = new AsyncSemaphore(5, 10);

            var acquire = semaphore.WaitAndRelease();
            Assert.Equal(4, semaphore.CurrentCount);
            acquire.Dispose();
            Assert.Equal(5, semaphore.CurrentCount);

            acquire = semaphore.WaitAndRelease(5);
            Assert.Equal(0, semaphore.CurrentCount);
            acquire.Dispose();
            Assert.Equal(5, semaphore.CurrentCount);
        }

        [Fact]
        public async Task WaitAndReleaseAsync()
        {
            using var semaphore = new AsyncSemaphore(5, 10);

            var acquire = await semaphore.WaitAndReleaseAsync();
            Assert.Equal(4, semaphore.CurrentCount);
            acquire.Dispose();
            Assert.Equal(5, semaphore.CurrentCount);

            acquire = await semaphore.WaitAndReleaseAsync(5);
            Assert.Equal(0, semaphore.CurrentCount);
            acquire.Dispose();
            Assert.Equal(5, semaphore.CurrentCount);
        }

        [Fact]
        public void WaitAndReleaseAll()
        {
            using var semaphore = new AsyncSemaphore(10, 10);

            var acquire = semaphore.WaitAndReleaseAll();
            Assert.Equal(0, semaphore.CurrentCount);
            acquire.Dispose();
            Assert.Equal(10, semaphore.CurrentCount);
        }

        [Fact]
        public async Task WaitAndReleaseAllAsync()
        {
            using var semaphore = new AsyncSemaphore(10, 10);

            var acquire = await semaphore.WaitAndReleaseAllAsync();
            Assert.Equal(0, semaphore.CurrentCount);
            acquire.Dispose();
            Assert.Equal(10, semaphore.CurrentCount);
        }

        [Fact]
        public void Wait_Timeout()
        {
            var startTime = Environment.TickCount;
            using var semaphore = new AsyncSemaphore(0, 1);
            var acquiredSemaphore = semaphore.Wait(1, TimeSpan.FromMilliseconds(250));
            var timeWaited = Environment.TickCount - startTime;

            Assert.False(acquiredSemaphore);
            Assert.True(timeWaited >= 90);
        }

        [Fact]
        public async Task WaitAsync_Timeout()
        {
            var startTime = Environment.TickCount;
            using var semaphore = new AsyncSemaphore(0, 1);
            var acquiredSemaphore = await semaphore.WaitAsync(1, TimeSpan.FromMilliseconds(100));
            var timeWaited = Environment.TickCount - startTime;

            Assert.False(acquiredSemaphore);
            Assert.True(timeWaited >= 90);
        }

        [Fact]
        public async Task WaitAsync_Priority()
        {
            var currentOrder = false; // We alternate the order of the two waiter acquires
            (Task<bool> Normal, Task<bool> Priority) GenerateWaiterTasks()
            {
                using var semaphore = new AsyncSemaphore(0, 1);
                Task<bool> normalSemaphoreWaiter;
                Task<bool> prioritySemaphoreWaiter;
                if (currentOrder)
                {
                    normalSemaphoreWaiter = semaphore.WaitAsync(1, Timeout.InfiniteTimeSpan, AsyncSemaphore.DefaultPriority, CancellationToken.None);
                    prioritySemaphoreWaiter = semaphore.WaitAsync(1, Timeout.InfiniteTimeSpan, AsyncSemaphore.DefaultPriority+1, CancellationToken.None);
                }
                else
                {
                    prioritySemaphoreWaiter = semaphore.WaitAsync(1, Timeout.InfiniteTimeSpan, AsyncSemaphore.DefaultPriority+1, CancellationToken.None);
                    normalSemaphoreWaiter = semaphore.WaitAsync(1, Timeout.InfiniteTimeSpan, AsyncSemaphore.DefaultPriority, CancellationToken.None);
                }
                currentOrder = !currentOrder;
                semaphore.Release(1);

                return (normalSemaphoreWaiter, prioritySemaphoreWaiter);
            }

            for (var i = 0; i < 10000; ++i)
            {
                var (normalSemaphoreWaiter, prioritySemaphoreWaiter) = GenerateWaiterTasks();
                var finishedWaiter = await Task.WhenAny(normalSemaphoreWaiter, prioritySemaphoreWaiter);
                Assert.Equal(prioritySemaphoreWaiter, finishedWaiter);
                Assert.True(finishedWaiter.Result);
                Assert.True(prioritySemaphoreWaiter.IsCompletedSuccessfully);
                Assert.False(normalSemaphoreWaiter.IsCompletedSuccessfully);
            }
        }
        
        [Fact]
        public void Wait_CancellationToken()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            var start = Environment.TickCount;
            using var cancellationToken = new CancellationTokenSource(100);
            Assert.Throws<OperationCanceledException>(() 
                => semaphore.Wait(cancellationToken.Token));
            Assert.True(Environment.TickCount - start >= 90);
        }

        [Fact]
        public async Task WaitAsync_CancellationToken()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            var start = Environment.TickCount;
            using var cancellationTokenSource = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<OperationCanceledException>(() => semaphore.WaitAsync(cancellationTokenSource.Token));
            Assert.True(Environment.TickCount - start >= 90);
        }
        
        [Fact]
        public void WaitAndRelease_Dispose()
        {
            using var semaphore = new AsyncSemaphore(1, 1);

            Assert.Equal(1, semaphore.CurrentCount);
            using (semaphore.WaitAndRelease())
            {
                Assert.Equal(0, semaphore.CurrentCount);
            }
            Assert.Equal(1, semaphore.CurrentCount);
        }

        [Fact]
        public async Task WaitAndReleaseAsync_Dispose()
        {
            using var semaphore = new AsyncSemaphore(1, 1);

            Assert.Equal(1, semaphore.CurrentCount);
            using (await semaphore.WaitAndReleaseAsync())
            {
                Assert.Equal(0, semaphore.CurrentCount);
            }
            Assert.Equal(1, semaphore.CurrentCount);
        }

        [Fact]
        public void Wait_CancellationToken_Success()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellationToken = new CancellationTokenSource(100);
            Assert.Throws<OperationCanceledException>(() => semaphore.Wait(cancellationToken.Token));
        }

        [Fact]
        public async Task WaitAsync_CancellationToken_Success()
        {
            using var semaphore = new AsyncSemaphore(0, 1);
            using var cancellationTokenSource = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<OperationCanceledException>(() => semaphore.WaitAsync(cancellationTokenSource.Token));
        }

        [Fact]
        public async Task Wait_Parallel_Success()
        {
            var lockObject = new object();
            var beforeCount = 0;
            var afterCount = 0;
            void IncrementBefore() { lock (lockObject) { beforeCount++; } }
            void IncrementAfter() { lock (lockObject) { afterCount++; } }

            using var semaphore = new AsyncSemaphore(100, 100);
            var parallelOptions = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 200
            };
            var waitTask = Parallel.ForEachAsync(Enumerable.Range(0, 200), parallelOptions, async (_, ct) =>
            {
                IncrementBefore();
                await semaphore.WaitAsync(ct);
                IncrementAfter();
            });

            // Wait for the first 100 semaphore requests to acquire and the last 100 to be pending
            while (true)
            {
                lock (lockObject)
                {
                    if (beforeCount == 200 && afterCount == 100) break;
                }
                await Task.Delay(100);
            }
            lock (lockObject)
            {
                Assert.Equal(100, semaphore._queuedAcquireRequests.Values.Sum(q => q.Count));
                Assert.Equal(0, semaphore.CurrentCount);
                Assert.Equal(200, beforeCount);
                Assert.Equal(100, afterCount);
            }

            // Now release 100 and ensure all 100 remaining semaphore requests acquire.
            foreach (var _ in Enumerable.Range(0, 100))
            {
                semaphore.Release();
            }
            await waitTask;
            Assert.Equal(0, semaphore.CurrentCount);
            Assert.Empty(semaphore._queuedAcquireRequests);
        }

        [Fact]
        public async Task AcquireUpTo()
        {
            var maxCount = 50;
            for (var startingCount = 0; startingCount <= maxCount; ++startingCount)
            {
                using var semaphore = new AsyncSemaphore(startingCount, maxCount);

                var acquireAmount = semaphore.AcquireUpTo(maxCount);
                Assert.Equal(0, semaphore.CurrentCount);
                Assert.Equal(startingCount, acquireAmount);
                var releaseAmount = semaphore.ReleaseUpTo(maxCount);
                Assert.Equal(maxCount, semaphore.CurrentCount);
                Assert.Equal(maxCount, releaseAmount);
            }
        }
    }
}
