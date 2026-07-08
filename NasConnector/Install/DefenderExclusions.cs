using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace NasConnector
{
    public enum ExclusionResult
    {
        Added,          // exclusions were added successfully
        AlreadyCovered, // nothing to do — every path was already excluded
        UserCancelled,  // the user dismissed the UAC prompt
        Failed          // powershell/Defender error
    }

    // Windows Defender blocks the copy/extract of cracked game files mid-install and the
    // file operation fails with ERROR_VIRUS_INFECTED. The share is never modified, so the
    // fix is to add the NAS source root and the local install root to Defender's PATH
    // exclusion list (real-time protection is never disabled — only path exclusions).
    //
    // Editing Defender preferences requires elevation, which Playnite doesn't have, so
    // Add-MpPreference runs in a short elevated powershell.exe child (one UAC prompt).
    // Reading the current exclusions (Get-MpPreference) works unelevated, so we can avoid
    // prompting when a path is already covered.
    public static class DefenderExclusions
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // ERROR_VIRUS_INFECTED (Win32 225). File.Create/stream writes that Defender blocks
        // surface as an IOException whose HResult is this value.
        private const int E_VIRUS_INFECTED = unchecked((int)0x800700E1);
        private const int WIN32_VIRUS_INFECTED = 225;
        private const int ERROR_CANCELLED = 1223; // user dismissed the UAC prompt

        public static bool IsVirusBlock(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is IOException && e.HResult == E_VIRUS_INFECTED)
                    return true;
                if (e is Win32Exception w && w.NativeErrorCode == WIN32_VIRUS_INFECTED)
                    return true;
            }
            return false;
        }

        public static bool IsProcessElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not determine elevation state.");
                return false;
            }
        }

        // Reads (Get-MpPreference).ExclusionPath. Runs unelevated. Returns null when Defender
        // or PowerShell isn't available — the caller then treats coverage as "unknown" and
        // still offers to add (Add-MpPreference simply no-ops on paths already present).
        public static IReadOnlyList<string> GetCurrentExclusions()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " +
                                "\"(Get-MpPreference).ExclusionPath -join '|'\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        return null;

                    return stdout
                        .Split(new[] { '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not read current Defender exclusions.");
                return null;
            }
        }

        public static bool IsExcluded(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true; // nothing to exclude

            var current = GetCurrentExclusions();
            if (current == null)
                return false; // unknown — let the caller try to add it

            var target = Normalize(path);
            foreach (var ex in current)
            {
                var excl = Normalize(ex);
                // A parent-folder exclusion covers everything beneath it.
                if (target.Equals(excl, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(excl + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static List<string> FilterUncovered(IEnumerable<string> paths)
        {
            var current = GetCurrentExclusions();
            var result = new List<string>();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (current != null)
                {
                    var target = Normalize(path);
                    var covered = current.Any(ex =>
                    {
                        var excl = Normalize(ex);
                        return target.Equals(excl, StringComparison.OrdinalIgnoreCase) ||
                               target.StartsWith(excl + "\\", StringComparison.OrdinalIgnoreCase);
                    });
                    if (covered)
                        continue;
                }

                if (!result.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    result.Add(path);
            }
            return result;
        }

        // Adds every given path to Defender's exclusion list via ONE elevated powershell.exe
        // (a single UAC prompt). Returns AlreadyCovered when there's nothing to add.
        public static ExclusionResult AddExclusions(IEnumerable<string> paths)
        {
            var toAdd = FilterUncovered(paths);
            if (toAdd.Count == 0)
                return ExclusionResult.AlreadyCovered;

            // Build a single-quoted PowerShell array literal, escaping embedded single quotes.
            // The whole script is then Base64/-EncodedCommand'd so path contents can never be
            // interpreted as PowerShell — no injection from user-controlled settings paths.
            var literals = toAdd.Select(p => "'" + p.Replace("'", "''") + "'");
            var script = "Add-MpPreference -ExclusionPath " + string.Join(",", literals);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            try
            {
                // Modifying Defender preferences requires elevation. When Playnite is already
                // running as admin we can do it silently in-process; otherwise we relaunch
                // powershell with Verb=runas, which raises a single UAC prompt.
                var elevated = IsProcessElevated();
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
                                "-EncodedCommand " + encoded,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                if (elevated)
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                }
                else
                {
                    psi.Verb = "runas";         // trigger the one-time UAC elevation
                    psi.UseShellExecute = true; // required for Verb=runas
                }

                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0)
                    {
                        logger.Info($"Added Defender exclusions ({(elevated ? "in-process" : "elevated")}): " +
                            $"{string.Join(", ", toAdd)}");
                        return ExclusionResult.Added;
                    }

                    logger.Error($"Add-MpPreference exited with code {p.ExitCode}.");
                    return ExclusionResult.Failed;
                }
            }
            catch (Win32Exception w) when (w.NativeErrorCode == ERROR_CANCELLED)
            {
                logger.Warn("User dismissed the UAC prompt for Defender exclusions.");
                return ExclusionResult.UserCancelled;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to add Defender exclusions.");
                return ExclusionResult.Failed;
            }
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path.TrimEnd('\\', '/');
            }
        }
    }
}
