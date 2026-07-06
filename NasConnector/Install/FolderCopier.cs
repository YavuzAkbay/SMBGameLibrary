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
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var dests = new List<string>(files.Length);
            foreach (var file in files)
            {
                var relative = file.Substring(sourceDir.Length).TrimStart('\\', '/');
                dests.Add(Path.Combine(destDir, relative));
            }
            CopyByBytes(files, dests, progress, cancel);
        }

        public static void CopyFiles(List<string> sourcePaths, string destDir,
            GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(destDir);
            var sources = new string[sourcePaths.Count];
            var dests = new string[sourcePaths.Count];
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                sources[i] = sourcePaths[i];
                dests[i] = Path.Combine(destDir, Path.GetFileName(sourcePaths[i]));
            }
            CopyByBytes(sources, dests, progress, cancel);
        }

        // Copies source->dest pairs streaming by bytes so the progress bar advances
        // smoothly even when a single file is tens of GB. Each file copy is retried on
        // a transient SMB error via IoRetry.
        private static void CopyByBytes(IList<string> sources, IList<string> dests,
            GlobalProgressActionArgs progress, CancellationToken cancel)
        {
            long totalBytes = 0;
            for (int i = 0; i < sources.Count; i++)
                totalBytes += new FileInfo(sources[i]).Length;

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
