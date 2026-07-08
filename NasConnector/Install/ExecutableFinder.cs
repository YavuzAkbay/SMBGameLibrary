using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NasConnector
{
    public static class ExecutableFinder
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // All plausible game executables under installDir (redists/launchers/helpers
        // filtered out), best-guess first, for the controller-friendly "pick the exe"
        // fallback dialog. Ordered by the same score FindPlayExecutable uses so the most
        // likely game exe sits at the top of the picker.
        public static List<string> GetCandidateExecutables(string installDir, string gameName)
        {
            return ScoreCandidates(installDir, gameName)
                .Select(c => c.File.FullName)
                .ToList();
        }

        // Every exe under installDir, unfiltered — the last-resort fallback so a
        // controller-only user can always pick something rather than being left with an
        // installed-but-unplayable game when the skip filters remove all candidates.
        public static List<string> GetAllExecutables(string installDir)
        {
            if (!Directory.Exists(installDir))
                return new List<string>();

            return Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .OrderByDescending(p => new FileInfo(p).Length)
                .ToList();
        }

        public static string FindPlayExecutable(string installDir, string gameName)
        {
            var best = ScoreCandidates(installDir, gameName).FirstOrDefault();
            if (best == null)
                return null;

            logger.Info($"NasConnector picked exe '{best.File.FullName}' " +
                $"(score {best.Score}: {string.Join(", ", best.Reasons)})");
            return best.File.FullName;
        }

        // ---- Scoring ------------------------------------------------------------

        private class Candidate
        {
            public FileInfo File;
            public int Score;
            public readonly List<string> Reasons = new List<string>();
        }

        // Score every non-skipped exe by combining several cheap signals (game-name match,
        // engine layout, folder, size, PE subsystem) and return them highest-first.
        private static List<Candidate> ScoreCandidates(string installDir, string gameName)
        {
            if (!Directory.Exists(installDir))
                return new List<Candidate>();

            var all = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(p => !ShouldSkip(p))
                .Select(p => new FileInfo(p))
                .ToList();

            if (!all.Any())
                return new List<Candidate>();

            // If any exe is >= 1 MB, discard the tiny ones — they are helpers/launchers.
            // Small indie games are preserved when no larger exe exists.
            var pool = all.Any(fi => fi.Length >= SmallExeThreshold)
                ? all.Where(fi => fi.Length >= SmallExeThreshold).ToList()
                : all;

            long maxLength = pool.Max(fi => fi.Length);
            var gameNorm = Normalize(NameCleaner.Clean(gameName ?? string.Empty));

            // PE inspection opens the file, so only inspect the biggest handful — the
            // real game is virtually always among them, and this avoids reading dozens
            // of engine helper exes.
            var peInspectSet = new HashSet<string>(
                pool.OrderByDescending(fi => fi.Length)
                    .Take(8)
                    .Select(fi => fi.FullName),
                StringComparer.OrdinalIgnoreCase);

            var candidates = new List<Candidate>();
            foreach (var fi in pool)
            {
                var c = new Candidate { File = fi };

                // Filename matches the game's own name — the strongest signal.
                if (gameNorm.Length >= 3 && NameMatches(gameNorm, fi.Name))
                {
                    c.Score += 100;
                    c.Reasons.Add("name match");
                }

                // Engine layout conventions.
                if (HasUnityDataSibling(fi))
                {
                    c.Score += 90;
                    c.Reasons.Add("unity _Data");
                }
                if (IsUnrealShipping(fi))
                {
                    c.Score += 85;
                    c.Reasons.Add("unreal shipping");
                }
                if (HasPckSibling(fi))
                {
                    c.Score += 70;
                    c.Reasons.Add("godot/gm .pck");
                }

                // Folder position.
                if (IsKnownBinFolder(fi.DirectoryName))
                {
                    c.Score += 20;
                    c.Reasons.Add("bin folder");
                }
                else if (string.Equals(fi.DirectoryName, installDir,
                    StringComparison.OrdinalIgnoreCase))
                {
                    c.Score += 10;
                    c.Reasons.Add("install root");
                }

                // Size bonus, scaled to the largest candidate (0..15).
                if (maxLength > 0)
                {
                    int sizeBonus = (int)(15.0 * fi.Length / maxLength);
                    c.Score += sizeBonus;
                }

                // Console-subsystem exes are almost never the game.
                if (peInspectSet.Contains(fi.FullName)
                    && PeInspector.GetSubsystem(fi.FullName) == PeSubsystem.Console)
                {
                    c.Score -= 60;
                    c.Reasons.Add("console subsystem");
                }

                // Soft-skip words not caught by the hard skip list.
                if (ContainsNearSkipWord(fi.Name))
                {
                    c.Score -= 30;
                    c.Reasons.Add("near-skip word");
                }

                candidates.Add(c);
            }

            // Highest score first; tie-break on size so the bigger exe wins.
            return candidates
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.File.Length)
                .ToList();
        }

        // ---- Signals ------------------------------------------------------------

        private static bool NameMatches(string gameNorm, string exeFileName)
        {
            var exeNorm = Normalize(Path.GetFileNameWithoutExtension(exeFileName));
            if (exeNorm.Length < 3)
                return false;

            if (exeNorm == gameNorm)
                return true;

            // One contained in the other, as long as the shorter side is substantial —
            // avoids matching on a 3-letter fragment.
            var shorter = Math.Min(exeNorm.Length, gameNorm.Length);
            return shorter >= 4 &&
                (exeNorm.Contains(gameNorm) || gameNorm.Contains(exeNorm));
        }

        private static bool HasUnityDataSibling(FileInfo exe)
        {
            var dataDir = Path.Combine(exe.DirectoryName,
                Path.GetFileNameWithoutExtension(exe.Name) + "_Data");
            return Directory.Exists(dataDir);
        }

        private static bool IsUnrealShipping(FileInfo exe)
        {
            var name = Path.GetFileNameWithoutExtension(exe.Name);
            if (!name.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))
                return false;

            // Confirm it sits under a Binaries/Win64 (or Win32) path.
            var parent = Path.GetFileName(exe.DirectoryName.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var grandParent = Path.GetFileName(
                Path.GetDirectoryName(exe.DirectoryName) ?? string.Empty);
            bool inWinBin = string.Equals(parent, "Win64", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parent, "Win32", StringComparison.OrdinalIgnoreCase);
            bool underBinaries = string.Equals(grandParent, "Binaries",
                StringComparison.OrdinalIgnoreCase);
            return inWinBin && underBinaries;
        }

        private static bool HasPckSibling(FileInfo exe)
        {
            try
            {
                return Directory.EnumerateFiles(exe.DirectoryName, "*.pck").Any();
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        // ---- Static data --------------------------------------------------------

        private static readonly string[] SkipPatterns =
        {
            "setup", "install", "uninstall", "unins", "redist",
            "vc_redist", "vcredist", "directx", "dxsetup", "dxwebsetup", "_commonredist",
            "dotnet", "crashpad", "crash_reporter", "crashreportclient",
            // launchers / updaters
            "launcher", "updater", "patcher",
            // anti-cheat / DRM service exes
            "easyanticheat", "eac_launcher", "battleye", "be_service",
            "upc", "uplay", "steam_api",
            // prerequisite installers bundled inside game folders
            "prereq", "prerequisite", "ue4prereq", "ue5prereq",
            "physx", "oalinst", "apisetup",
            // embedded runtimes / helper processes
            "cefsubprocess", "subprocess", "cef", "notification_helper",
            "ffmpeg", "python", "nvngx", "handbrake"
        };

        // Words that suggest a helper/tool but are too broad to hard-skip on (they can
        // legitimately appear in a game exe name). Penalized rather than removed.
        private static readonly string[] NearSkipWords =
        {
            "helper", "tool", "editor", "server", "benchmark", "config", "settings",
            "report", "diagnostic"
        };

        // Drop exes smaller than this only when larger candidates exist.
        // Filters out bootstrappers / helpers while preserving small indie game exes.
        private const long SmallExeThreshold = 1_048_576; // 1 MB

        private static readonly string[] BinFolderNames =
        {
            "bin", "bin64", "bin32", "win64", "win32", "x64", "x86", "game", "games"
        };

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

        private static bool ContainsNearSkipWord(string fileName)
        {
            var lower = Path.GetFileNameWithoutExtension(fileName).ToLower();
            return NearSkipWords.Any(w => lower.Contains(w));
        }
    }
}
