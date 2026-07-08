using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace NasConnector
{
    public class NasInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private NasGameEntry entry;
        private readonly Func<CancellationToken, NasGameEntry> resolveEntry;
        private readonly NasConnectorSettings settings;
        private readonly IPlayniteAPI api;

        // Leave a little headroom so we never fill the drive to the last byte.
        private const long FreeSpaceMarginBytes = 1024L * 1024L * 1024L; // 1 GB

        // Set by the worker when the game's exe couldn't be auto-detected; the picker
        // is then shown AFTER the progress dialog closes (so it isn't stacked under it).
        private string pendingExePickDir;

        public NasInstallController(Game game, NasGameEntry entry,
            Func<CancellationToken, NasGameEntry> resolveEntry,
            NasConnectorSettings settings, IPlayniteAPI api) : base(game)
        {
            this.entry = entry;
            this.resolveEntry = resolveEntry;
            this.settings = settings;
            this.api = api;
            Name = "Install from share";
        }

        public override void Install(InstallActionArgs args)
        {
            string installDir = Path.Combine(
                settings.LocalInstallPath, SanitizeName(Game.Name));

            pendingExePickDir = null;

            // Everything below happens inside the progress dialog so the user always sees
            // an animated status and can cancel. Nothing heavy (SMB connect, share walk,
            // free-space read) runs on the UI thread before the dialog appears — that was
            // the "app frozen, is it crashed?" window.
            api.Dialogs.ActivateGlobalProgress(progressArgs =>
            {
                var cancel = progressArgs.CancelToken;
                try
                {
                    // Phase 1: make sure we know what to install. On a cache miss this
                    // triggers the SMB connect + share scan — indeterminate spinner keeps
                    // the UI alive and the Cancel button responsive throughout.
                    progressArgs.IsIndeterminate = true;
                    progressArgs.Text = $"Connecting to {settings.NasBasePath}…";

                    if (entry == null && resolveEntry != null)
                        entry = resolveEntry(cancel);

                    if (entry == null)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            api.Dialogs.ShowErrorMessage(
                                $"{Game.Name} could not be found on the share anymore.\n\n" +
                                "Refresh the SMB Game Library and try again.",
                                "SMB Game Library"));
                        return;
                    }

                    cancel.ThrowIfCancellationRequested();

                    // Phase 2: free-space pre-check (reads the NAS tree / archive header).
                    // Was previously on the UI thread before any feedback.
                    progressArgs.Text = "Checking available space…";
                    if (!HasEnoughFreeSpace(out long requiredBytes, out long freeBytes))
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            api.Dialogs.ShowErrorMessage(
                                $"Not enough free space to install {Game.Name}.\n\n" +
                                $"Needs about {FormatGb(requiredBytes)}, but only {FormatGb(freeBytes)} is free " +
                                $"on {Path.GetPathRoot(installDir)}.\n\nFree up some space or choose a different " +
                                "install drive in the SMB Game Library settings.",
                                "SMB Game Library"));
                        return; // game stays uninstalled
                    }

                    cancel.ThrowIfCancellationRequested();

                    // Phase 3: copy / extract. The helpers switch the bar to a real % with
                    // live speed once they've computed totals.
                    progressArgs.Text = $"Preparing {Game.Name}…";
                    RunCopyOrExtract(installDir, progressArgs, cancel);
                }
                catch (OperationCanceledException)
                {
                    // Install was cut off mid-way — remove the local copy.
                    // The share source is only ever read from, never touched.
                    TryCleanup(installDir);
                }
                catch (Exception ex) when (DefenderExclusions.IsVirusBlock(ex))
                {
                    // Windows Defender raised a false positive on a game file (common with
                    // custom packers / unsigned launchers) and aborted the copy/extract. Add the
                    // share + install roots to Defender's exclusions (one UAC prompt), then retry
                    // — instead of the scary raw error.
                    logger.Warn(ex, $"Defender blocked install of {Game.Name}");
                    TryCleanup(installDir);
                    HandleVirusBlock(installDir, progressArgs, cancel);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Install failed for {Game.Name}");
                    TryCleanup(installDir);
                    Application.Current.Dispatcher.Invoke(() =>
                        api.Dialogs.ShowErrorMessage(
                            $"Installation failed:\n{ex.Message}", "SMB Game Library"));
                }
            },
            new GlobalProgressOptions($"Installing {Game.Name}...", cancelable: true));

            // The progress dialog has now closed. If we couldn't pick the exe
            // automatically, let the user choose it with the controller.
            if (pendingExePickDir != null)
                PromptForExecutable(pendingExePickDir);
        }

        // The actual copy/extract, factored out so the virus-block handler can retry it once
        // after adding Defender exclusions.
        private void RunCopyOrExtract(string installDir, GlobalProgressActionArgs progressArgs,
            CancellationToken cancel)
        {
            switch (entry.GameType)
            {
                case NasGameType.PreInstalledFolder:
                    Directory.CreateDirectory(installDir);
                    FolderCopier.CopyFolder(entry.NasFolderPath, installDir,
                        progressArgs, cancel);
                    CompleteInstall(installDir, null);
                    break;

                case NasGameType.SingleArchive:
                    ArchiveInstaller.ExtractArchive(entry.NasArchivePath, installDir,
                        progressArgs, cancel);
                    CompleteInstall(installDir, null);
                    break;
            }
        }

        // Recovery for a Defender virus-block: add the NAS share root and the local install
        // root to Defender's path exclusions (single UAC prompt), then retry the install once.
        // The retry calls RunCopyOrExtract directly — a second block therefore falls through
        // to the generic error handler rather than looping back here.
        private void HandleVirusBlock(string installDir, GlobalProgressActionArgs progressArgs,
            CancellationToken cancel)
        {
            var paths = new[] { settings.LocalInstallPath, settings.NasBasePath };

            progressArgs.IsIndeterminate = true;
            progressArgs.Text = "Windows Defender blocked a file — adding an exclusion…";

            var result = DefenderExclusions.AddExclusions(paths);
            switch (result)
            {
                case ExclusionResult.Added:
                case ExclusionResult.AlreadyCovered:
                    try
                    {
                        progressArgs.Text = $"Retrying {Game.Name}…";
                        Directory.CreateDirectory(installDir);
                        RunCopyOrExtract(installDir, progressArgs, cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        TryCleanup(installDir);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Install retry failed for {Game.Name}");
                        TryCleanup(installDir);
                        Application.Current.Dispatcher.Invoke(() =>
                            api.Dialogs.ShowErrorMessage(
                                DefenderExclusions.IsVirusBlock(ex)
                                    ? $"Windows Defender is still blocking {Game.Name} even after adding an " +
                                      "exclusion.\n\nOpen Windows Security → Virus & threat protection → " +
                                      "Exclusions and confirm your NAS and install folders are listed, then " +
                                      "try again."
                                    : $"Installation failed:\n{ex.Message}",
                                "SMB Game Library"));
                    }
                    break;

                case ExclusionResult.UserCancelled:
                    TryCleanup(installDir);
                    Application.Current.Dispatcher.Invoke(() =>
                        api.Dialogs.ShowMessage(
                            $"Windows Defender blocked {Game.Name} — often a false positive on game files " +
                            "(custom packers, unsigned launchers).\n\n" +
                            "Installation was cancelled because the Defender exclusion wasn't approved. " +
                            "You can add the exclusion any time from the SMB Game Library settings " +
                            "(\"Add my library & install folders to Windows Defender exclusions\") and try again.",
                            "SMB Game Library"));
                    break;

                default: // Failed
                    TryCleanup(installDir);
                    Application.Current.Dispatcher.Invoke(() =>
                        api.Dialogs.ShowErrorMessage(
                            $"Windows Defender blocked {Game.Name}, and the exclusion couldn't be added " +
                            "automatically.\n\nAdd your NAS and install folders manually in Windows Security → " +
                            "Virus & threat protection → Exclusions, then try again.",
                            "SMB Game Library"));
                    break;
            }
        }

        private void CompleteInstall(string installDir, string knownExe)
        {
            var exePath = knownExe ?? ExecutableFinder.FindPlayExecutable(installDir, Game.Name);

            InvokeOnInstalled(new GameInstalledEventArgs
            {
                InstalledInfo = new GameInstallationData
                {
                    InstallDirectory = installDir
                }
            });

            if (exePath != null)
            {
                PushPlayAction(installDir, exePath);
            }
            else
            {
                // Files are in place but we couldn't pick the game exe automatically.
                // Defer to a controller-navigable picker shown once the progress dialog
                // closes (see the end of Install).
                pendingExePickDir = installDir;
            }
        }

        // ---- Play-action wiring -------------------------------------------------

        private void PushPlayAction(string installDir, string exePath)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var game = api.Database.Games.Get(Game.Id);
                    if (game == null) return;

                    if (game.GameActions == null)
                        game.GameActions = new ObservableCollection<GameAction>();

                    var oldPlay = game.GameActions.FirstOrDefault(a => a.IsPlayAction);
                    if (oldPlay != null) game.GameActions.Remove(oldPlay);

                    game.GameActions.Add(new GameAction
                    {
                        Type = GameActionType.File,
                        Path = exePath,
                        WorkingDir = Path.GetDirectoryName(exePath),
                        IsPlayAction = true,
                        Name = "Play"
                    });

                    api.Database.Games.Update(game);
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to push play action");
            }
        }

        // Controller-navigable exe picker for when auto-detection fails. Runs on the UI
        // thread; the Description carries the path relative to installDir so the chosen
        // item maps straight back to a full path.
        private void PromptForExecutable(string installDir)
        {
            var candidates = ExecutableFinder.GetCandidateExecutables(installDir, Game.Name);
            if (candidates.Count == 0)
            {
                // The skip filters removed every candidate. Rather than leave a
                // controller-only user with an unplayable game and no recovery, fall back
                // to offering ALL exes in the picker below.
                candidates = ExecutableFinder.GetAllExecutables(installDir);
            }

            if (candidates.Count == 0)
            {
                api.Notifications.Add(new NotificationMessage(
                    "nas-no-exe-" + Game.Id,
                    $"SMB Game Library: {Game.Name} installed, but no game executable was found. " +
                    "Set the Play action manually in the game's details.",
                    NotificationType.Info));
                return;
            }

            Func<string, GenericItemOption> toOption = full =>
                new GenericItemOption(Path.GetFileName(full), RelativeTo(installDir, full));

            GenericItemOption selected = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var options = candidates.Select(toOption).ToList();
                selected = api.Dialogs.ChooseItemWithSearch(
                    options,
                    search => candidates
                        .Where(full => Path.GetFileName(full)
                            .IndexOf(search ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(toOption)
                        .ToList(),
                    string.Empty,
                    $"Select the game to launch for {Game.Name}");
            });

            if (selected == null)
            {
                api.Notifications.Add(new NotificationMessage(
                    "nas-no-exe-" + Game.Id,
                    $"SMB Game Library: no Play action was set for {Game.Name}. " +
                    "Open the game's details to pick one whenever you like.",
                    NotificationType.Info));
                return;
            }

            var chosen = Path.Combine(installDir, selected.Description);
            PushPlayAction(installDir, chosen);
        }

        // ---- Free-space pre-check ----------------------------------------------

        private bool HasEnoughFreeSpace(out long requiredBytes, out long freeBytes)
        {
            requiredBytes = 0;
            freeBytes = 0;
            try
            {
                requiredBytes = EstimateRequiredBytes();
                if (requiredBytes <= 0)
                    return true; // couldn't estimate — don't block the install

                var root = Path.GetPathRoot(Path.GetFullPath(settings.LocalInstallPath));
                freeBytes = new DriveInfo(root).AvailableFreeSpace;
                return freeBytes >= requiredBytes + FreeSpaceMarginBytes;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Free-space pre-check failed; allowing install to proceed.");
                return true;
            }
        }

        private long EstimateRequiredBytes()
        {
            switch (entry.GameType)
            {
                case NasGameType.PreInstalledFolder:
                    return DirectorySize(entry.NasFolderPath);
                case NasGameType.SingleArchive:
                    return ArchiveInstaller.GetUncompressedSize(entry.NasArchivePath);
                default:
                    return 0;
            }
        }

        private static long DirectorySize(string dir)
        {
            // Read each file's length from the directory walk itself — over SMB the
            // enumeration already returns sizes, so this avoids a separate per-file
            // round-trip (the old GetFiles + new FileInfo pattern did one query per file).
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(fi => fi.Length);
        }

        // ---- Helpers ------------------------------------------------------------

        private static string RelativeTo(string baseDir, string fullPath)
        {
            var trimmed = fullPath.Substring(baseDir.Length).TrimStart('\\', '/');
            return trimmed.Length > 0 ? trimmed : Path.GetFileName(fullPath);
        }

        private static string FormatGb(long bytes)
        {
            const double GB = 1024.0 * 1024.0 * 1024.0;
            return $"{bytes / GB:F1} GB";
        }

        private static void TryCleanup(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { }
        }

        private static string SanitizeName(string name)
        {
            return string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        }
    }
}
