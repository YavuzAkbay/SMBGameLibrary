using Playnite.SDK;
using Playnite.SDK.Data;
using System.Collections.Generic;

namespace NasConnector
{
    public class NasConnectorSettings : ObservableObject
    {
        private string nasBasePath = @"\\192.168.178.46\disk1\Games";
        public string NasBasePath { get => nasBasePath; set => SetValue(ref nasBasePath, value); }

        private string localInstallPath = @"C:\Games";
        public string LocalInstallPath { get => localInstallPath; set => SetValue(ref localInstallPath, value); }

        private string smbUsername = string.Empty;
        public string SmbUsername { get => smbUsername; set => SetValue(ref smbUsername, value); }

        // Stored as plain text — acceptable for home NAS (no cloud sync of this file)
        private string smbPassword = string.Empty;
        public string SmbPassword { get => smbPassword; set => SetValue(ref smbPassword, value); }

        private string excludedFolders = string.Empty;
        public string ExcludedFolders { get => excludedFolders; set => SetValue(ref excludedFolders, value); }
    }

    public class NasConnectorSettingsViewModel : ObservableObject, ISettings
    {
        private readonly NasConnectorPlugin plugin;
        private NasConnectorSettings editingClone;

        private NasConnectorSettings settings;
        public NasConnectorSettings Settings
        {
            get => settings;
            set { settings = value; OnPropertyChanged(); }
        }

        private string connectionStatus = string.Empty;
        public string ConnectionStatus
        {
            get => connectionStatus;
            set { connectionStatus = value; OnPropertyChanged(); }
        }

        public RelayCommand TestConnectionCommand { get; }
        public RelayCommand BrowseLocalPathCommand { get; }

        public NasConnectorSettingsViewModel(NasConnectorPlugin plugin)
        {
            this.plugin = plugin;
            var saved = plugin.LoadPluginSettings<NasConnectorSettings>();
            Settings = saved ?? new NasConnectorSettings();

            TestConnectionCommand = new RelayCommand(() =>
            {
                var scanner = new NasLibraryScanner(Settings);
                var (success, message) = scanner.TestConnection();
                ConnectionStatus = message;
                plugin.PlayniteApi.Dialogs.ShowMessage(message, "NAS Connector");
            });

            // Playnite's native folder picker — controller-navigable, unlike the old
            // WinForms FolderBrowserDialog.
            BrowseLocalPathCommand = new RelayCommand(() =>
            {
                var chosen = plugin.PlayniteApi.Dialogs.SelectFolder();
                if (!string.IsNullOrEmpty(chosen))
                    Settings.LocalInstallPath = chosen;
            });
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Settings.NasBasePath))
                errors.Add("NAS path cannot be empty.");
            if (string.IsNullOrWhiteSpace(Settings.LocalInstallPath))
                errors.Add("Local install path cannot be empty.");
            return errors.Count == 0;
        }
    }
}
