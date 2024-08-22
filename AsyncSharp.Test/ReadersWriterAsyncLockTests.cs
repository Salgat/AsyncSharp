using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    public class ReadersWriterAsyncLockTests
    {
        [Fact]
        public void AcquireReader_UpgradeToWriter_CancellationToken()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock();

            using var upgradeableLock = readersWriterAsyncLock.AcquireUpgradeableReader();
            using var writerLock = upgradeableLock.UpgradeToWriter();
        }

        [Fact]
        public async Task AcquireReaderAsync_UpgradeToWriterAsync_CancellationToken()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock();

            using var upgradeableLock = await readersWriterAsyncLock.AcquireUpgradeableReaderAsync();
            using var writerLock = await upgradeableLock.UpgradeToWriterAsync();
        }

        [Fact]
        public void AcquireReadersAndWriter_CancellationToken()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
            
            using (readersWriterAsyncLock.AcquireReader())
            using (readersWriterAsyncLock.AcquireReader())
            using (readersWriterAsyncLock.AcquireReader())
            {
                var start = Environment.TickCount;
                using var cancellationTokenSource = new CancellationTokenSource(100);
                Assert.Throws<OperationCanceledException>(() => readersWriterAsyncLock.AcquireWriter(cancellationTokenSource.Token));
                Assert.True(Environment.TickCount - start >= 90);
            }
        }

        [Fact]
        public async Task AcquireReadersAndWriterAsync_CancellationToken()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock();

            using (await readersWriterAsyncLock.AcquireReaderAsync())
            using (await readersWriterAsyncLock.AcquireReaderAsync())
            using (await readersWriterAsyncLock.AcquireReaderAsync())
            {
                var start = Environment.TickCount;
                using var cancellationTokenSource = new CancellationTokenSource(100);
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    readersWriterAsyncLock.AcquireWriterAsync(cancellationTokenSource.Token));
                Assert.True(Environment.TickCount - start >= 90);
            }
        }

        [Fact]
        public async Task AcquireReadersAndWritersParallel()
        {
            var lockObject = new object();
            var beforeCount = 0;
            var duringCount = 0;
            var afterCount = 0;
            void IncrementBeforeReaderLock() { lock (lockObject) { beforeCount++; } }
            void IncrementAfterReaderLock() { lock (lockObject) { duringCount++; } }
            void IncrementAfterWriterLock() { lock (lockObject) { afterCount++; } }

            using var readersWriterAsyncLock = new ReadersWriterAsyncLock();
            var readerLock = new SemaphoreSlim(0, 2);
            var writerLock = new SemaphoreSlim(0, 2);

            using var cancellationTokenSource = new CancellationTokenSource();
            var parallelOptions = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationTokenSource.Token
            };
            var waitTask = Parallel.ForEachAsync(Enumerable.Range(0, 2), parallelOptions, async (_, ct) =>
            {
                IncrementBeforeReaderLock();
                using var upgradeableLock = await readersWriterAsyncLock.AcquireUpgradeableReaderAsync(ct);
                IncrementAfterReaderLock();
                await readerLock.WaitAsync(ct);
                using var upgradedWriterLock = await upgradeableLock.UpgradeToWriterAsync(ct);
                IncrementAfterWriterLock();
                await writerLock.WaitAsync(ct);
            });

            // Both readers should be acquired and waiting on the semaphoreslim
            while (true)
            {
                lock (lockObject)
                {
                    if (duringCount == 2) break;
                }
                await Task.Delay(100);
            }
            Assert.Equal(2, readersWriterAsyncLock._asyncSemaphore.MaxCount - readersWriterAsyncLock._asyncSemaphore.CurrentCount);

            // Release both, and both should not have acquired the writer
            readerLock.Release();
            readerLock.Release();
            await Task.Delay(100);
            Assert.Equal(0, afterCount);
        }

        [Fact]
        public async Task ManyReadersInParallel()
        {
            var lockObject = new object();
            var readsAcquiredCount = 0;
            var writesAcquiredCount = 0;
            var hasReader = new Dictionary<int, bool>();

            using var readerWriterUpgradeableLock = new ReadersWriterAsyncLock();
            var random = new Random();
            const int parallelThreads = 10;
            var parallelOptions = new ParallelOptions()
            {
                MaxDegreeOfParallelism = parallelThreads
            };
            await Parallel.ForEachAsync(Enumerable.Range(0, parallelThreads), parallelOptions, async (index, ct) =>
            {
                while (true)
                {
                    if (index != 0) await Task.Delay(random.Next(20), ct); // Give the writer some room to enter
                    using var readerLock = await readerWriterUpgradeableLock.AcquireUpgradeableReaderAsync(ct);
                    lock (lockObject)
                    {
                        hasReader[index] = true;
                        readsAcquiredCount++;
                    }
                    if (index != 0) await Task.Delay(random.Next(2), ct); // Give the writer some room to enter
                    lock (lockObject)
                    {
                        if (writesAcquiredCount == 100) return;
                        if (index != 0)
                        {
                            hasReader[index] = false;
                            continue; // Only one thread should be acquiring writer
                        }
                    }

                    using var writerLock = await readerLock.UpgradeToWriterAsync(ct);
                    lock (lockObject)
                    {
                        if (hasReader.Any(h => h.Key != 0 && h.Value == true))
                        {
                            throw new Exception("Another reader exists while writer is acquired");
                        }
                        if (writesAcquiredCount == 100) return;
                        writesAcquiredCount++;
                    }
                    await Task.Delay(100, ct);
                }
            });
        }
    }
}
