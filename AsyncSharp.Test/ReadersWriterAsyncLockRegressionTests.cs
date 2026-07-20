using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    /// <summary>
    /// Regression coverage for upgrade ownership, liveness, validation, and lease lifecycle.
    /// Every blocked operation has bounded cleanup so a regression fails instead of hanging the suite.
    /// </summary>
    public class ReadersWriterAsyncLockRegressionTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task UpgradeToWriterAsync_WhenWriterIsAlreadyQueued_DoesNotDeadlock()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(
                2,
                ReadersWriterAsyncLock.LockPriority.FirstInFirstOut);
            var upgradeable = await readersWriterLock.AcquireUpgradeableReaderAsync();
            using var queuedWriterCancellation =
                new CancellationTokenSource(CleanupTimeout);
            var queuedWriterTask =
                readersWriterLock.AcquireWriterAsync(queuedWriterCancellation.Token);

            IDisposable upgradedWriter = null;
            Exception upgradeException = null;
            try
            {
                using var upgradeCancellation = new CancellationTokenSource(Timeout);
                upgradeException = await Record.ExceptionAsync(async () =>
                {
                    upgradedWriter =
                        await upgradeable.UpgradeToWriterAsync(upgradeCancellation.Token);
                });
            }
            finally
            {
                queuedWriterCancellation.Cancel();
                var queuedWriter = await ObserveLeaseAsync(queuedWriterTask);
                queuedWriter?.Dispose();
                upgradedWriter?.Dispose();
                upgradeable.Dispose();
            }

            Assert.Null(upgradeException);
            Assert.NotNull(upgradedWriter);
        }

        [Fact]
        public async Task AcquireUpgradeableReaderAsync_SecondOwnerWaitsForFirstOwner()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var first = await readersWriterLock.AcquireUpgradeableReaderAsync();
            ReadersWriterAsyncLock.UpgradeableReaderAsyncLock second = null;
            using var secondCancellation = new CancellationTokenSource(Timeout);

            var secondException = await Record.ExceptionAsync(async () =>
            {
                second = await readersWriterLock.AcquireUpgradeableReaderAsync(
                    secondCancellation.Token);
            });

            second?.Dispose();
            first.Dispose();

            Assert.Null(second);
            Assert.IsAssignableFrom<OperationCanceledException>(secondException);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(0, false)]
        [InlineData(-1, true)]
        [InlineData(-1, false)]
        public void Constructor_NonPositiveMaxReaders_ThrowsWithCorrectParamName(
            int maxReaders,
            bool fair)
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var ignored = new ReadersWriterAsyncLock(maxReaders, fair);
            });

            Assert.Equal("maxReaders", exception.ParamName);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void PriorityConstructor_NonPositiveMaxReaders_ThrowsWithCorrectParamName(
            int maxReaders)
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var ignored = new ReadersWriterAsyncLock(
                    maxReaders,
                    ReadersWriterAsyncLock.LockPriority.FirstInFirstOut);
            });

            Assert.Equal("maxReaders", exception.ParamName);
        }

        [Fact]
        public void PriorityConstructor_InvalidLockPriority_ThrowsWithCorrectParamName()
        {
            var exception = Record.Exception(() =>
            {
                using var ignored = new ReadersWriterAsyncLock(
                    2,
                    (ReadersWriterAsyncLock.LockPriority)int.MaxValue);
            });

            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("lockPriority", outOfRange.ParamName);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void AcquireUpgradeableReaders_NonPositiveReaderCount_ThrowsWithCorrectParamName(
            int readerCount)
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);

            var exception = Record.Exception(() =>
            {
                using var ignored =
                    readersWriterLock.AcquireUpgradeableReaders(readerCount);
            });

            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("readerCount", outOfRange.ParamName);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task AcquireUpgradeableReadersAsync_NonPositiveReaderCount_ThrowsWithCorrectParamName(
            int readerCount)
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);

            var exception = await Record.ExceptionAsync(async () =>
            {
                using var ignored =
                    await readersWriterLock.AcquireUpgradeableReadersAsync(readerCount);
            });

            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("readerCount", outOfRange.ParamName);
        }

        [Fact]
        public void UpgradeToWriter_AfterUpgradeableReaderDisposed_Throws()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable = readersWriterLock.AcquireUpgradeableReader();
            upgradeable.Dispose();

            var exception = Record.Exception(() =>
            {
                using var ignored = upgradeable.UpgradeToWriter();
            });

            Assert.IsType<ObjectDisposedException>(exception);
        }

        [Fact]
        public async Task UpgradeToWriterAsync_AfterUpgradeableReaderDisposed_Throws()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReaderAsync();
            upgradeable.Dispose();
            using var timeoutSource = new CancellationTokenSource(Timeout);

            var exception = await Record.ExceptionAsync(async () =>
            {
                using var ignored = await upgradeable.UpgradeToWriterAsync(
                    timeoutSource.Token);
            });

            Assert.IsType<ObjectDisposedException>(exception);
        }

        [Fact]
        public void UpgradeToWriter_AfterParentDisposed_WhenNoAdditionalCountIsNeeded_Throws()
        {
            var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable = readersWriterLock.AcquireUpgradeableReaders(2);
            readersWriterLock.Dispose();
            IDisposable upgradedWriter = null;

            var exception = Record.Exception(() =>
                upgradedWriter = upgradeable.UpgradeToWriter());

            Record.Exception(() => upgradedWriter?.Dispose());
            Record.Exception(upgradeable.Dispose);
            Assert.Null(upgradedWriter);
            Assert.IsType<ObjectDisposedException>(exception);
        }

        [Fact]
        public async Task UpgradeToWriterAsync_AfterParentDisposed_WhenNoAdditionalCountIsNeeded_Throws()
        {
            var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReadersAsync(2);
            readersWriterLock.Dispose();
            IDisposable upgradedWriter = null;

            var exception = await Record.ExceptionAsync(async () =>
                upgradedWriter = await upgradeable.UpgradeToWriterAsync());

            Record.Exception(() => upgradedWriter?.Dispose());
            Record.Exception(upgradeable.Dispose);
            Assert.Null(upgradedWriter);
            Assert.IsType<ObjectDisposedException>(exception);
        }

        [Fact]
        public async Task DisposeUpgradeableReader_WhileUpgradeActive_PreservesExclusivity()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReaderAsync();
            var upgradedWriter = await upgradeable.UpgradeToWriterAsync();

            Exception outerDisposeException = null;
            Exception competingReaderException = null;
            Exception retryDisposeException = null;
            try
            {
                outerDisposeException = Record.Exception(upgradeable.Dispose);

                using var readerCancellation = new CancellationTokenSource(Timeout);
                competingReaderException = await Record.ExceptionAsync(async () =>
                {
                    using var competingReader =
                        await readersWriterLock.AcquireReaderAsync(
                            readerCancellation.Token);
                });
            }
            finally
            {
                upgradedWriter.Dispose();
                retryDisposeException = Record.Exception(upgradeable.Dispose);
            }

            Assert.IsType<InvalidOperationException>(outerDisposeException);
            Assert.IsAssignableFrom<OperationCanceledException>(
                competingReaderException);
            Assert.Null(retryDisposeException);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task DisposeUpgradeableReader_WhileUpgradePending_ThrowsAndCanBeRetried()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReaderAsync();
            IDisposable blockingReader =
                await readersWriterLock.AcquireReaderAsync();
            using var upgradeCancellation =
                new CancellationTokenSource(CleanupTimeout);
            using var queuedTimeout =
                new CancellationTokenSource(CleanupTimeout);
            var upgradeTask = upgradeable.UpgradeToWriterAsync(
                upgradeCancellation.Token);
            ReadersWriterAsyncLock.UpgradeableReaderAsyncLock nextOwner = null;
            Exception pendingDisposeException = null;
            Exception canceledUpgradeException = null;
            Exception retryDisposeException = null;
            Exception nextOwnerException = null;

            try
            {
                await WaitUntilAsync(
                    () => readersWriterLock._asyncSemaphore.QueuedWaiterCount == 1,
                    queuedTimeout.Token);

                pendingDisposeException = Record.Exception(upgradeable.Dispose);

                upgradeCancellation.Cancel();
                canceledUpgradeException = await Record.ExceptionAsync(async () =>
                {
                    using var ignored =
                        await upgradeTask.WaitAsync(CleanupTimeout);
                });

                Assert.Equal(0, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                retryDisposeException = Record.Exception(upgradeable.Dispose);

                using var nextOwnerTimeout =
                    new CancellationTokenSource(Timeout);
                nextOwnerException = await Record.ExceptionAsync(async () =>
                {
                    nextOwner =
                        await readersWriterLock.AcquireUpgradeableReaderAsync(
                            nextOwnerTimeout.Token);
                });
            }
            finally
            {
                upgradeCancellation.Cancel();
                var unexpectedWriter = await ObserveLeaseAsync(upgradeTask);
                unexpectedWriter?.Dispose();
                nextOwner?.Dispose();
                blockingReader?.Dispose();
                Record.Exception(upgradeable.Dispose);
            }

            Assert.IsType<InvalidOperationException>(pendingDisposeException);
            Assert.IsAssignableFrom<OperationCanceledException>(
                canceledUpgradeException);
            Assert.Null(retryDisposeException);
            Assert.Null(nextOwnerException);
            Assert.NotNull(nextOwner);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task UpgradeToWriter_CanceledAttempt_CanBeRetried()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable = readersWriterLock.AcquireUpgradeableReader();
            IDisposable blockingReader = readersWriterLock.AcquireReader();
            using var canceledAttempt =
                new CancellationTokenSource(CleanupTimeout);
            using var queuedTimeout =
                new CancellationTokenSource(CleanupTimeout);
            var canceledUpgradeTask = Task.Run(() =>
                upgradeable.UpgradeToWriter(canceledAttempt.Token));
            IDisposable upgradedWriter = null;
            Exception canceledUpgradeException = null;
            Exception retryException = null;
            var countWhileUpgraded = -1;

            try
            {
                await WaitUntilAsync(
                    () => readersWriterLock._asyncSemaphore.QueuedWaiterCount == 1,
                    queuedTimeout.Token);

                canceledAttempt.Cancel();
                canceledUpgradeException = await Record.ExceptionAsync(async () =>
                {
                    using var ignored =
                        await canceledUpgradeTask.WaitAsync(CleanupTimeout);
                });

                Assert.Equal(0, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                blockingReader.Dispose();
                blockingReader = null;

                using var retryTimeout = new CancellationTokenSource(Timeout);
                retryException = Record.Exception(() =>
                    upgradedWriter = upgradeable.UpgradeToWriter(
                        retryTimeout.Token));
                countWhileUpgraded = readersWriterLock._asyncSemaphore.CurrentCount;
            }
            finally
            {
                canceledAttempt.Cancel();
                var unexpectedWriter =
                    await ObserveLeaseAsync(canceledUpgradeTask);
                unexpectedWriter?.Dispose();
                upgradedWriter?.Dispose();
                blockingReader?.Dispose();
                upgradeable.Dispose();
            }

            Assert.IsAssignableFrom<OperationCanceledException>(
                canceledUpgradeException);
            Assert.Null(retryException);
            Assert.NotNull(upgradedWriter);
            Assert.Equal(0, countWhileUpgraded);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task AcquireUpgradeableReader_CanceledAfterGateAcquired_ReleasesGate()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            IDisposable blockingWriter = readersWriterLock.AcquireWriter();
            using var canceledAttempt =
                new CancellationTokenSource(CleanupTimeout);
            using var queuedTimeout =
                new CancellationTokenSource(CleanupTimeout);
            var canceledAcquireTask = Task.Run(() =>
                readersWriterLock.AcquireUpgradeableReader(
                    canceledAttempt.Token));
            ReadersWriterAsyncLock.UpgradeableReaderAsyncLock nextOwner = null;
            Exception canceledAcquireException = null;
            Exception nextOwnerException = null;

            try
            {
                await WaitUntilAsync(
                    () => readersWriterLock._asyncSemaphore.QueuedWaiterCount == 1,
                    queuedTimeout.Token);

                canceledAttempt.Cancel();
                canceledAcquireException = await Record.ExceptionAsync(async () =>
                {
                    using var ignored =
                        await canceledAcquireTask.WaitAsync(CleanupTimeout);
                });

                Assert.Equal(0, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                blockingWriter.Dispose();
                blockingWriter = null;

                using var nextOwnerTimeout =
                    new CancellationTokenSource(Timeout);
                nextOwnerException = Record.Exception(() =>
                    nextOwner = readersWriterLock.AcquireUpgradeableReader(
                        nextOwnerTimeout.Token));
            }
            finally
            {
                canceledAttempt.Cancel();
                var unexpectedOwner =
                    await ObserveLeaseAsync(canceledAcquireTask);
                unexpectedOwner?.Dispose();
                nextOwner?.Dispose();
                blockingWriter?.Dispose();
            }

            Assert.IsAssignableFrom<OperationCanceledException>(
                canceledAcquireException);
            Assert.Null(nextOwnerException);
            Assert.NotNull(nextOwner);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task AcquireUpgradeableReaderAsync_CanceledAfterGateAcquired_ReleasesGate()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            IDisposable blockingWriter = await readersWriterLock.AcquireWriterAsync();
            using var canceledAttempt =
                new CancellationTokenSource(CleanupTimeout);
            using var queuedTimeout =
                new CancellationTokenSource(CleanupTimeout);
            var canceledAcquireTask =
                readersWriterLock.AcquireUpgradeableReaderAsync(
                    canceledAttempt.Token);
            ReadersWriterAsyncLock.UpgradeableReaderAsyncLock nextOwner = null;
            Exception canceledAcquireException = null;
            Exception nextOwnerException = null;

            try
            {
                await WaitUntilAsync(
                    () => readersWriterLock._asyncSemaphore.QueuedWaiterCount == 1,
                    queuedTimeout.Token);

                canceledAttempt.Cancel();
                canceledAcquireException = await Record.ExceptionAsync(async () =>
                {
                    using var ignored =
                        await canceledAcquireTask.WaitAsync(CleanupTimeout);
                });

                Assert.Equal(0, readersWriterLock._asyncSemaphore.QueuedWaiterCount);
                blockingWriter.Dispose();
                blockingWriter = null;

                using var nextOwnerTimeout =
                    new CancellationTokenSource(Timeout);
                nextOwnerException = await Record.ExceptionAsync(async () =>
                {
                    nextOwner =
                        await readersWriterLock.AcquireUpgradeableReaderAsync(
                            nextOwnerTimeout.Token);
                });
            }
            finally
            {
                canceledAttempt.Cancel();
                var unexpectedOwner =
                    await ObserveLeaseAsync(canceledAcquireTask);
                unexpectedOwner?.Dispose();
                nextOwner?.Dispose();
                blockingWriter?.Dispose();
            }

            Assert.IsAssignableFrom<OperationCanceledException>(
                canceledAcquireException);
            Assert.Null(nextOwnerException);
            Assert.NotNull(nextOwner);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public async Task UpgradeToWriterAsync_CanceledAttempt_CanBeRetried()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReaderAsync();
            IDisposable blockingReader =
                await readersWriterLock.AcquireReaderAsync();
            IDisposable upgradedWriter = null;

            try
            {
                using var canceledAttempt =
                    new CancellationTokenSource(Timeout);
                var canceledException = await Record.ExceptionAsync(async () =>
                {
                    using var ignored = await upgradeable.UpgradeToWriterAsync(
                        canceledAttempt.Token);
                });

                Assert.IsAssignableFrom<OperationCanceledException>(
                    canceledException);

                blockingReader.Dispose();
                blockingReader = null;

                using var retryTimeout =
                    new CancellationTokenSource(Timeout);
                upgradedWriter = await upgradeable.UpgradeToWriterAsync(
                    retryTimeout.Token);

                Assert.NotNull(upgradedWriter);
                Assert.Equal(0, readersWriterLock._asyncSemaphore.CurrentCount);
            }
            finally
            {
                upgradedWriter?.Dispose();
                blockingReader?.Dispose();
                upgradeable.Dispose();
            }
        }

        [Fact]
        public async Task UpgradeToWriterAsync_ConcurrentAttempt_ThrowsInvalidOperationException()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReaderAsync();
            var blockingReader = await readersWriterLock.AcquireReaderAsync();
            using var firstCancellation =
                new CancellationTokenSource(CleanupTimeout);
            using var queuedTimeout = new CancellationTokenSource(Timeout);
            var firstUpgradeTask = upgradeable.UpgradeToWriterAsync(
                firstCancellation.Token);

            Exception concurrentException;
            try
            {
                await WaitUntilAsync(
                    () => readersWriterLock._asyncSemaphore.QueuedWaiterCount == 1,
                    queuedTimeout.Token);

                using var concurrentTimeout =
                    new CancellationTokenSource(Timeout);
                concurrentException = await Record.ExceptionAsync(async () =>
                {
                    using var ignored = await upgradeable.UpgradeToWriterAsync(
                        concurrentTimeout.Token);
                });
            }
            finally
            {
                firstCancellation.Cancel();
                var firstWriter = await ObserveLeaseAsync(firstUpgradeTask);
                firstWriter?.Dispose();
                blockingReader.Dispose();
                upgradeable.Dispose();
            }

            Assert.IsType<InvalidOperationException>(concurrentException);
        }

        [Fact]
        public async Task UpgradedWriter_DisposeTwice_IsIdempotent()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable =
                await readersWriterLock.AcquireUpgradeableReaderAsync();
            var upgradedWriter = await upgradeable.UpgradeToWriterAsync();

            upgradedWriter.Dispose();
            var secondWriterDisposeException =
                Record.Exception(upgradedWriter.Dispose);
            var countAfterWriterDispose =
                readersWriterLock._asyncSemaphore.CurrentCount;
            var outerDisposeException = Record.Exception(upgradeable.Dispose);

            Assert.Null(secondWriterDisposeException);
            Assert.Equal(1, countAfterWriterDispose);
            Assert.Null(outerDisposeException);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        [Fact]
        public void UpgradeableReader_DisposeTwice_IsIdempotent()
        {
            using var readersWriterLock = new ReadersWriterAsyncLock(2);
            var upgradeable = readersWriterLock.AcquireUpgradeableReader();

            upgradeable.Dispose();
            var secondDisposeException = Record.Exception(upgradeable.Dispose);

            Assert.Null(secondDisposeException);
            Assert.Equal(
                readersWriterLock.MaxReaders,
                readersWriterLock._asyncSemaphore.CurrentCount);
        }

        private static async Task<T> ObserveLeaseAsync<T>(Task<T> leaseTask)
            where T : IDisposable
        {
            try
            {
                return await leaseTask.WaitAsync(CleanupTimeout);
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                // Cleanup only: canceled or faulted tasks are observed.
                return default(T);
            }
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
