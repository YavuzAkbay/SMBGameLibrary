using Playnite.SDK;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace NasConnector
{
    public static class ArchiveInstaller
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // 1 MB streaming buffer — large enough to keep SMB throughput high,
        // small enough to report smooth byte-level progress on multi-GB files.
        private const int BufferSize = 1024 * 1024;

        public static void ExtractArchive(string archivePath, string destDir,
            GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(destDir);

            using (var archive = ArchiveFactory.Open(archivePath, new ReaderOptions()))
            {
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                ExtractEntries(entries, destDir, progress, cancel);
            }
        }

        // Total uncompressed size of a single archive — used for the free-space pre-check.
        public static long GetUncompressedSize(string archivePath)
        {
            using (var archive = ArchiveFactory.Open(archivePath, new ReaderOptions()))
                return archive.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        }

        // Streams each entry in chunks so progress advances by bytes (with live speed),
        // not by file count — a single multi-GB file no longer freezes the bar.
        private static void ExtractEntries(IEnumerable<IArchiveEntry> entries, string destDir,
            GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            long totalBytes = entries.Sum(e => e.Size);
            long bytesDone = 0;

            progress.ProgressMaxValue = totalBytes > 0 ? totalBytes : 1;
            progress.IsIndeterminate = totalBytes == 0;

            var sw = Stopwatch.StartNew();
            var buffer = new byte[BufferSize];

            // Resolve once so the zip-slip check below compares against a canonical root.
            var destRoot = Path.GetFullPath(destDir);

            foreach (var entry in entries)
            {
                cancel.ThrowIfCancellationRequested();

                var relativePath = entry.Key.Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.GetFullPath(Path.Combine(destDir, relativePath));

                // Zip-slip guard: never let a crafted entry (e.g. "..\..\foo") write
                // outside the install folder.
                if (!destPath.StartsWith(destRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(destPath, destRoot, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Warn($"Skipping archive entry outside install folder: {entry.Key}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath));

                var name = Path.GetFileName(destPath);
                long bytesBeforeEntry = bytesDone;

                // Retry the whole entry on a transient SMB read error (the source archive
                // lives on the NAS). On retry, rewind this entry's byte tally.
                IoRetry.Run(() =>
                {
                    bytesDone = bytesBeforeEntry;
                    using (var input = entry.OpenEntryStream())
                    using (var output = File.Create(destPath))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);

                            bytesDone += read;
                            progress.CurrentProgressValue = bytesDone;

                            double secs = sw.Elapsed.TotalSeconds;
                            double mbps = secs > 0 ? (bytesDone / 1048576.0) / secs : 0;
                            progress.Text =
                                $"Extracting: {name}  {FormatSize(bytesDone)} / {FormatSize(totalBytes)} — {mbps:F0} MB/s from NAS";
                        }
                    }
                }, cancel);
            }
        }

        private static string FormatSize(long bytes)
        {
            const double GB = 1024.0 * 1024.0 * 1024.0;
            const double MB = 1024.0 * 1024.0;
            if (bytes >= GB)
                return $"{bytes / GB:F2} GB";
            return $"{bytes / MB:F0} MB";
        }
    }
}
