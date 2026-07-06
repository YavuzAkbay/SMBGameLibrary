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
        private readonly NasGameEntry entry;
        private readonly NasConnectorSettings settings;
        private readonly IPlayniteAPI api;

        // Leave a little headroom so we never fill the drive to the last byte.
        private const long FreeSpaceMarginBytes = 1024L * 1024L * 1024L; // 1 GB

        // Set by the worker when the game's exe couldn't be auto-detected; the picker
        // is then shown AFTER the progress dialog closes (so it isn't stacked under it).
        private string pendingExePickDir;

        public NasInstallController(Game game, NasGameEntry entry,
            NasConnectorSettings settings, IPlayniteAPI api) : base(game)
        {
            this.entry = entry;
            this.settings = settings;
            this.api = api;
            Name = "Install from share";
        }

        public override void Install(InstallActionArgs args)
        {
            string installDir = Path.Combine(
                settings.LocalInstallPath, SanitizeName(Game.Name));

            // Pre-flight free-space check — abort cleanly with a clear message instead of
            // failing mid-copy and leaving a half-written folder behind.
            if (!HasEnoughFreeSpace(out long requiredBytes, out long freeBytes))
            {
                api.Dialogs.ShowErrorMessage(
                    $"Not enough free space to install {Game.Name}.\n\n" +
                    $"Needs about {FormatGb(requiredBytes)}, but only {FormatGb(freeBytes)} is free " +
                    $"on {Path.GetPathRoot(installDir)}.\n\nFree up some space or choose a different " +
                    "install drive in the SMB Game Library settings.",
                    "SMB Game Library");
                return; // game stays uninstalled
            }

            pendingExePickDir = null;

            api.Dialogs.ActivateGlobalProgress(progressArgs =>
            {
                try
                {
                    progressArgs.IsIndeterminate = false;
                    progressArgs.Text = $"Installing {Game.Name} from share...";

                    switch (entry.GameType)
                    {
                        case NasGameType.PreInstalledFolder:
                            Directory.CreateDirectory(installDir);
                            FolderCopier.CopyFolder(entry.NasFolderPath, installDir,
                                progressArgs, progressArgs.CancelToken);
                            CompleteInstall(installDir, null);
                            break;

                        case NasGameType.SingleArchive:
                            ArchiveInstaller.ExtractArchive(entry.NasArchivePath, installDir,
                                progressArgs, progressArgs.CancelToken);
                            CompleteInstall(installDir, null);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Install was cut off mid-way — remove the local copy.
                    // The share source is only ever read from, never touched.
                    TryCleanup(installDir);
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

        private void CompleteInstall(string installDir, string knownExe)
        {
            var exePath = knownExe ?? ExecutableFinder.FindPlayExecutable(installDir);

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
            var candidates = ExecutableFinder.GetCandidateExecutables(installDir);
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
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
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
