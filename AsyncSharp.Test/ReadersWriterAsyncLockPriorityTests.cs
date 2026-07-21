using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Deterministic ordering and lease-lifecycle coverage for the reader/writer lock policies.
    /// </summary>
    public class ReadersWriterAsyncLockPriorityTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Theory]
        [InlineData(ReadersWriterAsyncLock.LockPriority.Readers)]
        [InlineData(ReadersWriterAsyncLock.LockPriority.Writers)]
        public async Task ReaderAndWriterPriority_GrantsDocumentedTypeFirst(
            ReadersWriterAsyncLock.LockPriority lockPriority)
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2, lockPriority);
            var owner = await readersWriterLock.AcquireWriterAsync();
            using var readerCancellation = new CancellationTokenSource(Timeout);
            using var writerCancellation = new CancellationTokenSource(Timeout);

            Task<IDisposable> readerTask;
            Task<IDisposable> writerTask;
            if (lockPriority == ReadersWriterAsyncLock.LockPriority.Readers)
            {
                writerTask = readersWriterLock.AcquireWriterAsync(writerCancellation.Token);
                readerTask = readersWriterLock.AcquireReaderAsync(readerCancellation.Token);
            }
            else
            {
                readerTask = readersWriterLock.AcquireReaderAsync(readerCancellation.Token);
                writerTask = readersWriterLock.AcquireWriterAsync(writerCancellation.Token);
            }

            IDisposable reader = null;
            IDisposable writer = null;
            try
            {
                Assert.Equal(2, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                owner.Dispose();
                owner = null;

                var first = await Task.WhenAny(readerTask, writerTask).WaitAsync(Timeout);
                if (lockPriority == ReadersWriterAsyncLock.LockPriority.Readers)
                {
                    Assert.Same(readerTask, first);
                    reader = await readerTask.WaitAsync(Timeout);
                    Assert.False(writerTask.IsCompleted);
                    reader.Dispose();
                    reader = null;
                    writer = await writerTask.WaitAsync(Timeout);
                }
                else
                {
                    Assert.Same(writerTask, first);
                    writer = await writerTask.WaitAsync(Timeout);
                    Assert.False(readerTask.IsCompleted);
                    writer.Dispose();
                    writer = null;
                    reader = await readerTask.WaitAsync(Timeout);
                }
            }
            finally
            {
                readerCancellation.Cancel();
                writerCancellation.Cancel();
                reader?.Dispose();
                writer?.Dispose();
                owner?.Dispose();
                await ObserveLeaseAsync(readerTask);
                await ObserveLeaseAsync(writerTask);
            }

            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task FirstInFirstOut_BlockedWriterPreventsLaterReaderFromBypassing()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(
                2,
                ReadersWriterAsyncLock.LockPriority.FirstInFirstOut);
            var activeReader = await readersWriterLock.AcquireReaderAsync();
            using var writerCancellation = new CancellationTokenSource(Timeout);
            using var readerCancellation = new CancellationTokenSource(Timeout);
            var writerTask = readersWriterLock.AcquireWriterAsync(writerCancellation.Token);
            var laterReaderTask = readersWriterLock.AcquireReaderAsync(readerCancellation.Token);
            IDisposable writer = null;
            IDisposable laterReader = null;

            try
            {
                Assert.Equal(2, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                Assert.False(laterReaderTask.IsCompleted);

                activeReader.Dispose();
                activeReader = null;
                writer = await writerTask.WaitAsync(Timeout);
                Assert.False(laterReaderTask.IsCompleted);

                writer.Dispose();
                writer = null;
                laterReader = await laterReaderTask.WaitAsync(Timeout);
            }
            finally
            {
                writerCancellation.Cancel();
                readerCancellation.Cancel();
                laterReader?.Dispose();
                writer?.Dispose();
                activeReader?.Dispose();
                await ObserveLeaseAsync(writerTask);
                await ObserveLeaseAsync(laterReaderTask);
            }

            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Theory]
        [InlineData(ReadersWriterAsyncLock.LockPriority.FirstInFirstOutUnfair)]
        [InlineData(ReadersWriterAsyncLock.LockPriority.Unfair)]
        public async Task UnfairPolicies_LaterReaderMayBypassBlockedWriter(
            ReadersWriterAsyncLock.LockPriority lockPriority)
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2, lockPriority);
            var activeReader = await readersWriterLock.AcquireReaderAsync();
            using var writerCancellation = new CancellationTokenSource(Timeout);
            var writerTask = readersWriterLock.AcquireWriterAsync(writerCancellation.Token);
            var laterReaderTask = readersWriterLock.AcquireReaderAsync();
            IDisposable writer = null;
            IDisposable laterReader = null;

            try
            {
                laterReader = await laterReaderTask.WaitAsync(Timeout);
                Assert.False(writerTask.IsCompleted);

                laterReader.Dispose();
                laterReader = null;
                Assert.False(writerTask.IsCompleted);
                activeReader.Dispose();
                activeReader = null;
                writer = await writerTask.WaitAsync(Timeout);
            }
            finally
            {
                writerCancellation.Cancel();
                writer?.Dispose();
                laterReader?.Dispose();
                activeReader?.Dispose();
                await ObserveLeaseAsync(writerTask);
                await ObserveLeaseAsync(laterReaderTask);
            }

            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Theory]
        [InlineData(ReadersWriterAsyncLock.LockPriority.Readers)]
        [InlineData(ReadersWriterAsyncLock.LockPriority.Writers)]
        [InlineData(ReadersWriterAsyncLock.LockPriority.FirstInFirstOut)]
        [InlineData(ReadersWriterAsyncLock.LockPriority.FirstInFirstOutUnfair)]
        [InlineData(ReadersWriterAsyncLock.LockPriority.Unfair)]
        public async Task Upgrade_HasPriorityOverAlreadyQueuedWriter(
            ReadersWriterAsyncLock.LockPriority lockPriority)
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2, lockPriority);
            var upgradeable = await readersWriterLock.AcquireUpgradeableReaderAsync();
            using var writerCancellation = new CancellationTokenSource(Timeout);
            using var upgradeCancellation = new CancellationTokenSource(Timeout);
            var writerTask = readersWriterLock.AcquireWriterAsync(writerCancellation.Token);
            IDisposable upgradedWriter = null;
            IDisposable queuedWriter = null;

            try
            {
                Assert.Equal(1, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                upgradedWriter = await upgradeable
                    .UpgradeToWriterAsync(upgradeCancellation.Token)
                    .WaitAsync(Timeout);

                Assert.False(writerTask.IsCompleted);
                Assert.Equal(0, readersWriterLock._asyncSemaphore.CurrentCount);

                upgradedWriter.Dispose();
                upgradedWriter = null;
                Assert.False(writerTask.IsCompleted);
                upgradeable.Dispose();
                queuedWriter = await writerTask.WaitAsync(Timeout);
            }
            finally
            {
                writerCancellation.Cancel();
                upgradeCancellation.Cancel();
                queuedWriter?.Dispose();
                upgradedWriter?.Dispose();
                Record.Exception(upgradeable.Dispose);
                await ObserveLeaseAsync(writerTask);
            }

            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task OrdinaryLease_DisposeTwice_IsIdempotent(
            bool writer,
            bool asynchronous)
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            IDisposable lease;
            if (writer)
            {
                lease = asynchronous
                    ? await readersWriterLock.AcquireWriterAsync()
                    : readersWriterLock.AcquireWriter();
            }
            else
            {
                lease = asynchronous
                    ? await readersWriterLock.AcquireReaderAsync()
                    : readersWriterLock.AcquireReader();
            }

            lease.Dispose();
            var secondDisposeException = Record.Exception(lease.Dispose);

            Assert.Null(secondDisposeException);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        private static async Task ObserveLeaseAsync<T>(Task<T> leaseTask)
            where T : IDisposable
        {
            try
            {
                var lease = await leaseTask.WaitAsync(Timeout);
                lease?.Dispose();
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
            }
        }
    }
}
