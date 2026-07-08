using Playnite.SDK;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NasConnector
{
    public static class FolderCopier
    {
        // 1 MB buffer — same as ArchiveInstaller: keeps SMB throughput high while
        // still reporting smooth byte-level progress on multi-GB files.
        private const int BufferSize = 1024 * 1024;

        public static void CopyFolder(string sourceDir, string destDir,
            GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            // Enumerating a large folder over SMB can take several seconds; keep the bar
            // animated so it doesn't read as "frozen" before CopyByBytes flips it to a %.
            progress.IsIndeterminate = true;
            progress.Text = "Scanning files on NAS…";

            // Walk the tree ONCE. DirectoryInfo.EnumerateFiles hands back FileInfo objects
            // whose Length is already populated from the directory enumeration, so we build
            // the source/dest lists AND the byte total from the same walk — no separate
            // per-file stat pass (each of which was its own SMB round-trip).
            var sources = new List<string>();
            var dests = new List<string>();
            long totalBytes = 0;
            int seen = 0;
            foreach (var fi in new DirectoryInfo(sourceDir)
                .EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if ((++seen & 0x3FF) == 0) // every 1024 files, stay cancellable
                    cancel.ThrowIfCancellationRequested();

                var relative = fi.FullName.Substring(sourceDir.Length).TrimStart('\\', '/');
                sources.Add(fi.FullName);
                dests.Add(Path.Combine(destDir, relative));
                totalBytes += fi.Length;
            }

            CopyByBytes(sources, dests, totalBytes, progress, cancel);
        }

        public static void CopyFiles(List<string> sourcePaths, string destDir,
            GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(destDir);
            var sources = new string[sourcePaths.Count];
            var dests = new string[sourcePaths.Count];
            long totalBytes = 0;
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                sources[i] = sourcePaths[i];
                dests[i] = Path.Combine(destDir, Path.GetFileName(sourcePaths[i]));
                totalBytes += new FileInfo(sourcePaths[i]).Length;
            }
            CopyByBytes(sources, dests, totalBytes, progress, cancel);
        }

        // Copies source->dest pairs streaming by bytes so the progress bar advances
        // smoothly even when a single file is tens of GB. Each file copy is retried on
        // a transient SMB error via IoRetry. totalBytes is supplied by the caller (read
        // from the same enumeration that built the lists) to avoid a second stat pass.
        private static void CopyByBytes(IList<string> sources, IList<string> dests,
            long totalBytes, GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            progress.ProgressMaxValue = totalBytes > 0 ? totalBytes : 1;
            progress.IsIndeterminate = totalBytes == 0;

            var sw = Stopwatch.StartNew();
            var buffer = new byte[BufferSize];
            long bytesDone = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var source = sources[i];
                var dest = dests[i];
                var name = Path.GetFileName(dest);
                long bytesBeforeFile = bytesDone;

                IoRetry.Run(() =>
                {
                    // Restart this file's byte tally on retry so progress stays accurate.
                    bytesDone = bytesBeforeFile;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));

                    using (var input = File.OpenRead(source))
                    using (var output = File.Create(dest))
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
                                $"Copying: {name}  {FormatSize(bytesDone)} / {FormatSize(totalBytes)} — {mbps:F0} MB/s from NAS";
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
