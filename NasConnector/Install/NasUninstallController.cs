using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
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

            try
            {
                Directory.Delete(installDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to delete {installDir}");
                api.Dialogs.ShowErrorMessage(
                    $"Could not fully delete {installDir}:\n{ex.Message}",
                    "NAS Connector");
            }

            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }
    }
}
