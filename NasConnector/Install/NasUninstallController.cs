using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace NasConnector
{
    public class NasUninstallController : UninstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;

        public NasUninstallController(Game game, IPlayniteAPI api) : base(game)
        {
            this.api = api;
            Name = "Uninstall (delete local copy — NAS untouched)";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var installDir = Game.InstallDirectory;

            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                InvokeOnUninstalled(new GameUninstalledEventArgs());
                return;
            }

            var result = api.Dialogs.ShowMessage(
                $"Delete local installation at:\n\n{installDir}\n\n" +
                "The copy on your NAS will NOT be deleted.",
                "NAS Connector — Uninstall",
                MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes)
                return;

            bool deleted = false;
            bool canceled = false;
            Exception failure = null;

            // Delete inside a progress dialog so a large (50GB+) local copy shows a moving
            // bar with a Cancel button instead of silently freezing the UI. Only the LOCAL
            // copy is touched — the share is never modified.
            api.Dialogs.ActivateGlobalProgress(progressArgs =>
            {
                var cancel = progressArgs.CancelToken;
                try
                {
                    progressArgs.IsIndeterminate = true;
                    progressArgs.Text = "Scanning local files…";
                    var files = Directory.GetFiles(installDir, "*", SearchOption.AllDirectories);

                    progressArgs.ProgressMaxValue = files.Length > 0 ? files.Length : 1;
                    progressArgs.IsIndeterminate = files.Length == 0;

                    for (int i = 0; i < files.Length; i++)
                    {
                        cancel.ThrowIfCancellationRequested();

                        var file = files[i];
                        // Retry transient SMB/AV locks, same as the install path does.
                        IoRetry.Run(() =>
                        {
                            // Clear read-only so a locked game file doesn't block deletion.
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }, cancel);

                        progressArgs.CurrentProgressValue = i + 1;
                        progressArgs.Text =
                            $"Removing {Path.GetFileName(file)} ({i + 1}/{files.Length})…";
                    }

                    // Files are gone — clear out the now-empty directory tree.
                    IoRetry.Run(() => Directory.Delete(installDir, recursive: true), cancel);
                    deleted = true;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            new GlobalProgressOptions($"Uninstalling {Game.Name}…", cancelable: true));

            if (canceled)
            {
                // Some local files may already be gone, but the share is intact. Leave the
                // game installed so the user can re-run uninstall (or reinstall) cleanly.
                logger.Info($"Uninstall of {Game.Name} was canceled by the user.");
                api.Dialogs.ShowMessage(
                    $"Uninstall of {Game.Name} was canceled. Some local files may have been " +
                    "removed; the game is still marked as installed. Run uninstall again to " +
                    "finish removing it.\n\nYour NAS copy was not touched.",
                    "NAS Connector — Uninstall");
                return;
            }

            if (!deleted)
            {
                logger.Error(failure, $"Failed to delete {installDir}");
                api.Dialogs.ShowErrorMessage(
                    $"Could not fully delete {installDir}:\n{failure?.Message}\n\n" +
                    "The game is still marked as installed. Free up the files and try again.",
                    "NAS Connector");
                return; // leave the game installed — files are still on disk
            }

            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }
    }
}
