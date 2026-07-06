using Playnite.SDK;
using System;
using System.IO;
using System.Threading;

namespace NasConnector
{
    // Small bounded retry for transient I/O over SMB. A momentary network hiccup
    // (a dropped packet, the NAS spinning a disk up) shouldn't abort a multi-GB
    // install — we retry a few times with a short backoff before giving up.
    // Cancellation is always honored immediately, even between attempts.
    public static class IoRetry
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private const int MaxAttempts = 3;
        private const int BackoffMs = 750;

        public static void Run(Action action, CancellationToken cancel)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    action();
                    return;
                }
                catch (IOException ex) when (attempt < MaxAttempts)
                {
                    logger.Warn(ex, $"Transient I/O error (attempt {attempt}/{MaxAttempts}); retrying.");
                    cancel.WaitHandle.WaitOne(BackoffMs);
                }
                catch (UnauthorizedAccessException ex) when (attempt < MaxAttempts)
                {
                    // Briefly-held locks (AV scan, indexer) surface as UnauthorizedAccess.
                    logger.Warn(ex, $"Transient access error (attempt {attempt}/{MaxAttempts}); retrying.");
                    cancel.WaitHandle.WaitOne(BackoffMs);
                }
            }
        }
    }
}
