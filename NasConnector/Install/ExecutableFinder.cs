using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NasConnector
{
    public static class ExecutableFinder
    {
        // All plausible game executables under installDir (redists/launchers/helpers
        // filtered out), for the controller-friendly "pick the exe" fallback dialog.
        public static List<string> GetCandidateExecutables(string installDir)
        {
            if (!Directory.Exists(installDir))
                return new List<string>();

            return Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(p => !ShouldSkip(p))
                .OrderByDescending(p => new FileInfo(p).Length) // biggest (most likely the game) first
                .ToList();
        }

        private static readonly string[] SkipPatterns =
        {
            "setup", "install", "uninstall", "unins", "redist",
            "vc_redist", "vcredist", "directx", "dxsetup", "_commonredist", "dotnet",
            "crashpad", "crash_reporter",
            // launchers / updaters
            "launcher", "updater", "patcher",
            // anti-cheat / DRM service exes
            "easyanticheat", "eac_launcher", "battleye", "be_service",
            "upc", "uplay", "steam_api",
            // prerequisite installers bundled inside game folders
            "prereq", "prerequisite", "ue4prereq", "ue5prereq",
            "physx", "oalinst", "apisetup"
        };

        // Drop exes smaller than this only when larger candidates exist.
        // Filters out bootstrappers / helpers while preserving small indie game exes.
        private const long SmallExeThreshold = 1_048_576; // 1 MB

        private static readonly string[] BinFolderNames =
        {
            "bin", "bin64", "bin32", "win64", "win32", "x64", "x86", "game", "games"
        };

        public static string FindPlayExecutable(string installDir)
        {
            var all = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(p => !ShouldSkip(p))
                .Select(p => new FileInfo(p))
                .ToList();

            if (!all.Any())
                return null;

            // If any exe is >= 1 MB, discard the tiny ones — they are helpers/launchers.
            var candidates = all.Any(fi => fi.Length >= SmallExeThreshold)
                ? all.Where(fi => fi.Length >= SmallExeThreshold).ToList()
                : all;

            if (candidates.Count == 1)
                return candidates[0].FullName;

            // Prefer exes in known binary subdirectories (bin, bin64, win64, x64, …)
            var binCandidates = candidates
                .Where(fi => IsKnownBinFolder(fi.DirectoryName))
                .ToList();
            if (binCandidates.Any())
                return binCandidates.OrderByDescending(fi => fi.Length).First().FullName;

            // Fall back to root-level exes, then largest overall
            var rootCandidates = candidates
                .Where(fi => string.Equals(fi.DirectoryName, installDir,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pool = rootCandidates.Any() ? rootCandidates : candidates;
            return pool.OrderByDescending(fi => fi.Length).First().FullName;
        }

        private static bool IsKnownBinFolder(string dirPath)
        {
            var folderName = Path.GetFileName(dirPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return BinFolderNames.Any(b =>
                string.Equals(b, folderName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldSkip(string path)
        {
            var lower = Path.GetFileNameWithoutExtension(path).ToLower();
            return SkipPatterns.Any(pat => lower.Contains(pat));
        }
    }
}
