using System;
using System.Threading;
using System.Threading.Tasks;
using AsyncSharp;

namespace PackageSmokeTest
{
    internal static class Program
    {
        private static async Task Main()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using (var semaphore = new AsyncSemaphore(1, 1, AsyncSemaphore.WaiterPriority.FirstInFirstOut))
            {
                using (semaphore.WaitAndRelease(cancellation.Token))
                {
                }

                using (await semaphore.WaitAndReleaseAsync(cancellation.Token).ConfigureAwait(false))
                {
                }
            }

            using (var mutex = new AsyncMutex())
            {
                using (mutex.LockAndUnlock(cancellation.Token))
                {
                }

                using (await mutex.LockAndUnlockAsync(cancellation.Token).ConfigureAwait(false))
                {
                }
            }

            using (var readersWriterLock = new ReadersWriterAsyncLock(
                4,
                ReadersWriterAsyncLock.LockPriority.FirstInFirstOut))
            {
                using (readersWriterLock.AcquireReader(cancellation.Token))
                {
                }

                using (await readersWriterLock.AcquireReaderAsync(cancellation.Token).ConfigureAwait(false))
                {
                }

                using (await readersWriterLock.AcquireWriterAsync(cancellation.Token).ConfigureAwait(false))
                {
                }

                using (var upgradeable = await readersWriterLock
                    .AcquireUpgradeableReadersAsync(2, cancellation.Token)
                    .ConfigureAwait(false))
                {
                    using (await upgradeable.UpgradeToWriterAsync(cancellation.Token).ConfigureAwait(false))
                    {
                    }
                }
            }
        }
    }
}
