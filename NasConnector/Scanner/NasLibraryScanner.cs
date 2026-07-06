using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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
                bool mounted = TryMountShare();
                if (Directory.Exists(settings.NasBasePath))
                    return (true, $"Connection successful. Found: {settings.NasBasePath}");
                return (false, $"Path not found: {settings.NasBasePath}");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        public List<NasGameEntry> ScanGames(CancellationToken cancelToken)
        {
            TryMountShare();

            var results = new List<NasGameEntry>();

            if (!Directory.Exists(settings.NasBasePath))
                throw new IOException($"NAS path not accessible: {settings.NasBasePath}");

            foreach (var dir in Directory.GetDirectories(settings.NasBasePath))
            {
                cancelToken.ThrowIfCancellationRequested();

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
            }

            return results;
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

            // Check for pre-installed game (has a non-setup .exe at top level)
            var gameExe = files.FirstOrDefault(f =>
                Path.GetExtension(f).ToLower() == ".exe" && !IsSkipExe(f));

            if (gameExe != null)
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

        // SMB authentication via WNetAddConnection2 when credentials are configured
        private bool TryMountShare()
        {
            if (string.IsNullOrEmpty(settings.SmbUsername))
                return true;

            try
            {
                var nr = new NETRESOURCE
                {
                    dwType = RESOURCETYPE_DISK,
                    lpRemoteName = settings.NasBasePath
                };
                int result = WNetAddConnection2(ref nr, settings.SmbPassword,
                    settings.SmbUsername, 0);
                if (result != 0 && result != ERROR_ALREADY_ASSIGNED)
                    logger.Warn($"WNetAddConnection2 returned {result} for {settings.NasBasePath}");
                return result == 0 || result == ERROR_ALREADY_ASSIGNED;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "SMB mount attempt failed");
                return false;
            }
        }

        private const int RESOURCETYPE_DISK = 1;
        private const int ERROR_ALREADY_ASSIGNED = 85;

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
