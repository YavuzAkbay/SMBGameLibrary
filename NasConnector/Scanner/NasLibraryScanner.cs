using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NasConnector
{
    public class NasLibraryScanner
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly string[] ArchiveExtensions = { ".7z", ".zip", ".rar" };
        private static readonly string[] ExeSkipPatterns =
        {
            "setup", "install", "uninstall", "unins", "redist",
            "vc_redist", "vcredist", "directx", "dxsetup", "_commonredist", "dotnet"
        };

        private readonly NasConnectorSettings settings;

        public NasLibraryScanner(NasConnectorSettings settings)
        {
            this.settings = settings;
        }

        public (bool success, string message) TestConnection()
        {
            try
            {
                int mountResult = TryMountShare(CancellationToken.None);
                if (IsAuthError(mountResult))
                    return (false, $"Authentication failed for {settings.NasBasePath} — {DescribeError(mountResult)}.");

                if (Directory.Exists(settings.NasBasePath))
                    return (true, $"Connection successful. Found: {settings.NasBasePath}");

                if (mountResult != 0 && mountResult != ERROR_ALREADY_ASSIGNED)
                    return (false, $"Could not connect to {settings.NasBasePath} — {DescribeError(mountResult)}.");

                return (false, $"Path not found: {settings.NasBasePath}");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        public List<NasGameEntry> ScanGames(CancellationToken cancelToken)
        {
            int mountResult = TryMountShare(cancelToken);
            if (IsAuthError(mountResult))
                throw new IOException(
                    $"Authentication failed for {settings.NasBasePath} — {DescribeError(mountResult)}. " +
                    "Check the username and password in the SMB Game Library settings.");

            if (!Directory.Exists(settings.NasBasePath))
                throw new IOException($"NAS path not accessible: {settings.NasBasePath}");

            // Classify each top-level folder in parallel. Every classification is a chain
            // of blocking SMB directory enumerations; running them concurrently overlaps
            // the per-call network latency instead of paying it serially. Cap the degree
            // of parallelism so we don't flood the share with connections.
            var results = new ConcurrentBag<NasGameEntry>();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancelToken
            };

            Parallel.ForEach(Directory.GetDirectories(settings.NasBasePath), options, dir =>
            {
                try
                {
                    var entry = ClassifyDirectory(dir);
                    if (entry != null)
                        results.Add(entry);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"Skipping directory due to error: {dir}");
                }
            });

            return results.ToList();
        }

        private NasGameEntry ClassifyDirectory(string dirPath)
        {
            var dirName = Path.GetFileName(dirPath);
            // Cleaned title for display + metadata matching; GameId/paths stay on the real name.
            var displayName = NameCleaner.Clean(dirName);
            var files = Directory.GetFiles(dirPath, "*", SearchOption.TopDirectoryOnly);

            // Check for a single archive file
            var archiveFile = files.FirstOrDefault(f =>
                ArchiveExtensions.Contains(Path.GetExtension(f).ToLower()));

            if (archiveFile != null)
            {
                return new NasGameEntry
                {
                    GameId = MakeGameId(dirPath),
                    DisplayName = displayName,
                    GameType = NasGameType.SingleArchive,
                    NasFolderPath = dirPath,
                    NasArchivePath = archiveFile
                };
            }

            // Check for pre-installed game (has a non-setup .exe at top level or nested
            // within a few subfolder levels — e.g. GameName\bin\win64\game.exe). Reuse the
            // top-level file list we already fetched to test for a top-level exe before
            // recursing, saving one SMB round-trip per folder in the common case.
            if (files.Any(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    && !IsSkipExe(f))
                || ContainsGameExe(dirPath, 0))
            {
                return new NasGameEntry
                {
                    GameId = MakeGameId(dirPath),
                    DisplayName = displayName,
                    GameType = NasGameType.PreInstalledFolder,
                    NasFolderPath = dirPath
                };
            }

            return null;
        }

        // top level = 0; recurse up to this many subfolder levels below it.
        private const int MaxExeSearchDepth = 4;

        // True if a non-setup .exe exists at dirPath or within MaxExeSearchDepth
        // subfolders. Lazy + short-circuiting to minimize SMB traversal; a single
        // inaccessible subfolder is skipped rather than discarding the whole game.
        private bool ContainsGameExe(string dirPath, int depth)
        {
            try
            {
                foreach (var exe in Directory.EnumerateFiles(dirPath, "*.exe",
                    SearchOption.TopDirectoryOnly))
                {
                    if (!IsSkipExe(exe))
                        return true;
                }

                if (depth >= MaxExeSearchDepth)
                    return false;

                foreach (var sub in Directory.EnumerateDirectories(dirPath))
                {
                    if (ContainsGameExe(sub, depth + 1))
                        return true;
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Skipping unreadable subtree while scanning: {dirPath}");
            }

            return false;
        }

        private static bool IsSkipExe(string path)
        {
            var lower = Path.GetFileNameWithoutExtension(path).ToLower();
            return ExeSkipPatterns.Any(p => lower.Contains(p));
        }

        private static string MakeGameId(string folderPath)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant()));
                return "nas_" + BitConverter.ToString(bytes).Replace("-", "").ToLower();
            }
        }

        // SMB authentication via WNetAddConnection2 when credentials are configured.
        // Returns the Win32 result code (0 = connected, 85 = already connected); any
        // other value is a failure the caller can map to a friendly message.
        private int TryMountShare(CancellationToken cancelToken)
        {
            if (string.IsNullOrEmpty(settings.SmbUsername))
                return 0;

            try
            {
                // WNetAddConnection2 is a blocking Win32 call that ignores our cancel
                // token — on an unreachable NAS it can hang for a long time. Run it on a
                // background task and poll the token so the caller's progress dialog and
                // its Cancel button stay responsive. If cancelled, we throw and let the
                // orphaned task finish harmlessly on its own.
                var nr = new NETRESOURCE
                {
                    dwType = RESOURCETYPE_DISK,
                    lpRemoteName = settings.NasBasePath
                };
                var task = Task.Run(() =>
                    WNetAddConnection2(ref nr, settings.SmbPassword, settings.SmbUsername, 0));

                while (!task.Wait(200))
                    cancelToken.ThrowIfCancellationRequested();

                int result = task.Result;
                if (result != 0 && result != ERROR_ALREADY_ASSIGNED)
                    logger.Warn($"WNetAddConnection2 returned {result} for {settings.NasBasePath}");
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "SMB mount attempt failed");
                return ERROR_EXTENDED_ERROR;
            }
        }

        // Wrong credentials / access-denied codes worth surfacing distinctly.
        private static bool IsAuthError(int code) =>
            code == ERROR_ACCESS_DENIED || code == ERROR_INVALID_PASSWORD ||
            code == ERROR_LOGON_FAILURE || code == ERROR_SESSION_CREDENTIAL_CONFLICT;

        private static string DescribeError(int code)
        {
            switch (code)
            {
                case ERROR_ACCESS_DENIED: return "access denied";
                case ERROR_INVALID_PASSWORD:
                case ERROR_LOGON_FAILURE: return "wrong username or password";
                case ERROR_SESSION_CREDENTIAL_CONFLICT:
                    return "conflicting credentials for this server (disconnect any existing connection)";
                case ERROR_BAD_NETPATH:
                case ERROR_BAD_NET_NAME: return "network path not found";
                default: return $"error code {code}";
            }
        }

        private const int RESOURCETYPE_DISK = 1;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_BAD_NETPATH = 53;
        private const int ERROR_BAD_NET_NAME = 67;
        private const int ERROR_ALREADY_ASSIGNED = 85;
        private const int ERROR_INVALID_PASSWORD = 86;
        private const int ERROR_EXTENDED_ERROR = 1208;
        private const int ERROR_LOGON_FAILURE = 1326;
        private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

        [StructLayout(LayoutKind.Sequential)]
        private struct NETRESOURCE
        {
            public int dwScope;
            public int dwType;
            public int dwDisplayType;
            public int dwUsage;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment;
            public string lpProvider;
        }

        [DllImport("mpr.dll")]
        private static extern int WNetAddConnection2(
            ref NETRESOURCE netResource, string password, string username, int flags);
    }
}
