using System;
using System.Collections.Generic;
using System.Linq;
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
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock(2);
            var firstReader = await readersWriterAsyncLock.AcquireReaderAsync();
            var secondReader = await readersWriterAsyncLock.AcquireReaderAsync();
            using var writerCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var writerTask =
                readersWriterAsyncLock.AcquireWriterAsync(writerCancellation.Token);
            IDisposable writer = null;

            try
            {
                Assert.False(writerTask.IsCompleted);

                firstReader.Dispose();
                firstReader = null;
                Assert.False(writerTask.IsCompleted);

                secondReader.Dispose();
                secondReader = null;

                writer = await writerTask;
                Assert.NotNull(writer);
            }
            finally
            {
                firstReader?.Dispose();
                secondReader?.Dispose();

                if (writer == null)
                {
                    writerCancellation.Cancel();
                    try
                    {
                        writer = await writerTask;
                    }
                    catch (OperationCanceledException)
                        when (writerCancellation.IsCancellationRequested)
                    {
                    }
                }

                writer?.Dispose();
            }
        }

        [Fact]
        public async Task ManyReadersInParallel()
        {
            var lockObject = new object();
            var readsAcquiredCount = 0;
            var writesAcquiredCount = 0;
            var hasReader = new Dictionary<int, bool>();

            using var readerWriterUpgradeableLock = new ReadersWriterAsyncLock();
            const int parallelThreads = 10;
            const int targetWrites = 25;
            using var timeoutSource =
                new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var parallelOptions = new ParallelOptions()
            {
                MaxDegreeOfParallelism = parallelThreads,
                CancellationToken = timeoutSource.Token
            };
            await Parallel.ForEachAsync(Enumerable.Range(0, parallelThreads), parallelOptions, async (index, ct) =>
            {
                while (true)
                {
                    if (index != 0) await Task.Delay(Random.Shared.Next(20), ct); // Give the writer some room to enter

                    ReadersWriterAsyncLock.UpgradeableReaderAsyncLock upgradeableLock = null;
                    IDisposable readerLock;
                    if (index == 0)
                    {
                        upgradeableLock =
                            await readerWriterUpgradeableLock.AcquireUpgradeableReaderAsync(ct);
                        readerLock = upgradeableLock;
                    }
                    else
                    {
                        readerLock =
                            await readerWriterUpgradeableLock.AcquireReaderAsync(ct);
                    }

                    using (readerLock)
                    {
                        lock (lockObject)
                        {
                            hasReader[index] = true;
                            readsAcquiredCount++;
                        }
                        if (index != 0) await Task.Delay(Random.Shared.Next(2), ct); // Give the writer some room to enter
                        lock (lockObject)
                        {
                            if (writesAcquiredCount == targetWrites) return;
                            if (index != 0)
                            {
                                hasReader[index] = false;
                                continue; // Only one thread should be acquiring writer
                            }
                        }

                        using var writerLock =
                            await upgradeableLock.UpgradeToWriterAsync(ct);
                        lock (lockObject)
                        {
                            if (hasReader.Any(h => h.Key != 0 && h.Value == true))
                            {
                                throw new Exception("Another reader exists while writer is acquired");
                            }
                            if (writesAcquiredCount == targetWrites) return;
                            writesAcquiredCount++;
                        }
                        await Task.Delay(20, ct);
                    }
                }
            });

            Assert.Equal(targetWrites, writesAcquiredCount);
            Assert.True(readsAcquiredCount >= targetWrites);
        }
    }
}
