using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Coverage for upgradeable readers that hold more than one reader count. Previously the sync overload silently
    /// acquired only 1, and the async overload deadlocked on upgrade because the upgrade requested MaxReaders - 1
    /// additional counts regardless of how many were already held.
    /// </summary>
    public class ReadersWriterAsyncLockUpgradeTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Fact]
        public void AcquireUpgradeableReaders_Sync_HonorsReaderCount()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock(5);

            using var upgradeable = readersWriterAsyncLock.AcquireUpgradeableReaders(3);

            // 3 of the 5 reader counts must actually be held.
            Assert.Equal(2, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public void AcquireUpgradeableReaders_Sync_MultiReader_UpgradeToWriterReachesExclusivity()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock(5);

            using (var upgradeable = readersWriterAsyncLock.AcquireUpgradeableReaders(3))
            {
                Assert.Equal(2, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
                using (var writer = upgradeable.UpgradeToWriter())
                {
                    // Exclusive: all reader counts consumed.
                    Assert.Equal(0, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
                }
                Assert.Equal(2, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
            }

            Assert.Equal(5, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task AcquireUpgradeableReadersAsync_MultiReader_UpgradeDoesNotDeadlock()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock(5);

            var upgradeable = await readersWriterAsyncLock.AcquireUpgradeableReadersAsync(3);
            try
            {
                Assert.Equal(2, readersWriterAsyncLock._asyncSemaphore.CurrentCount);

                // Before the fix this required 3 + (5 - 1) = 7 counts against a max of 5 and would never complete.
                var writer = await readersWriterAsyncLock_UpgradeWithTimeout(upgradeable);
                try
                {
                    Assert.Equal(0, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
                }
                finally
                {
                    writer.Dispose();
                }

                Assert.Equal(2, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
            }
            finally
            {
                upgradeable.Dispose();
            }

            Assert.Equal(5, readersWriterAsyncLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task AcquireUpgradeableReadersAsync_ReaderCountExceedingMax_Throws()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock(5);

            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()
                => readersWriterAsyncLock.AcquireUpgradeableReadersAsync(6));
            Assert.Equal("readerCount", ex.ParamName);
        }

        [Fact]
        public void AcquireUpgradeableReaders_ReaderCountExceedingMax_Throws()
        {
            using var readersWriterAsyncLock = new ReadersWriterAsyncLock(5);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(()
                => readersWriterAsyncLock.AcquireUpgradeableReaders(6));
            Assert.Equal("readerCount", ex.ParamName);
        }

        private static async Task<IDisposable> readersWriterAsyncLock_UpgradeWithTimeout(
            ReadersWriterAsyncLock.UpgradeableReaderAsyncLock upgradeable)
        {
            using var timeoutSource = new CancellationTokenSource(Timeout);
            return await upgradeable.UpgradeToWriterAsync(timeoutSource.Token);
        }
    }
}
