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
                int mountResult = TryMountShare();
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
            int mountResult = TryMountShare();
            if (IsAuthError(mountResult))
                throw new IOException(
                    $"Authentication failed for {settings.NasBasePath} — {DescribeError(mountResult)}. " +
                    "Check the username and password in the SMB Game Library settings.");

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

        // SMB authentication via WNetAddConnection2 when credentials are configured.
        // Returns the Win32 result code (0 = connected, 85 = already connected); any
        // other value is a failure the caller can map to a friendly message.
        private int TryMountShare()
        {
            if (string.IsNullOrEmpty(settings.SmbUsername))
                return 0;

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
                return result;
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
