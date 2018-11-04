using System;
using System.Collections.Generic;
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
            var readersWriterAsyncLock = new ReadersWriterAsyncLock();

            using (var upgradeableLock = readersWriterAsyncLock.AcquireUpgradeableReader())
            using (var writerLock = upgradeableLock.UpgradeToWriter())
            {
            }
        }

        [Fact]
        public async Task AcquireReaderAsync_UpgradeToWriterAsync_CancellationToken()
        {
            var readersWriterAsyncLock = new ReadersWriterAsyncLock();

            using (var upgradeableLock = await readersWriterAsyncLock.AcquireUpgradeableReaderAsync())
            using (var writerLock = await upgradeableLock.UpgradeToWriterAsync())
            {
            }
        }

        [Fact]
        public void AcquireReadersAndWriter_CancellationToken()
        {
            var readersWriterAsyncLock = new ReadersWriterAsyncLock();
            
            using (readersWriterAsyncLock.AcquireReader())
            using (readersWriterAsyncLock.AcquireReader())
            using (readersWriterAsyncLock.AcquireReader())
            {
                var start = DateTime.UtcNow;
                using (var cancellationTokenSource = new CancellationTokenSource(100))
                {
                    Assert.Throws<OperationCanceledException>(() =>
                        readersWriterAsyncLock.AcquireWriter(cancellationTokenSource.Token));
                    Assert.True(DateTime.UtcNow - start > TimeSpan.FromMilliseconds(100));
                }
            }
        }

        [Fact]
        public async Task AcquireReadersAndWriterAsync_CancellationToken()
        {
            var readersWriterAsyncLock = new ReadersWriterAsyncLock();

            using (await readersWriterAsyncLock.AcquireReaderAsync())
            using (await readersWriterAsyncLock.AcquireReaderAsync())
            using (await readersWriterAsyncLock.AcquireReaderAsync())
            {
                var start = DateTime.UtcNow;
                using (var cancellationTokenSource = new CancellationTokenSource(100))
                {
                    await Assert.ThrowsAsync<OperationCanceledException>(() =>
                        readersWriterAsyncLock.AcquireWriterAsync(cancellationTokenSource.Token));
                    Assert.True(DateTime.UtcNow - start > TimeSpan.FromMilliseconds(100));
                }
            }
        }
    }
}
