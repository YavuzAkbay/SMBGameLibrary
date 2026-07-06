using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace NasConnector
{
    public partial class NasConnectorSettingsView : System.Windows.Controls.UserControl
    {
        public NasConnectorSettingsView()
        {
            InitializeComponent();
        }

        private void BrowseLocalPath_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                var vm = DataContext as NasConnectorSettingsViewModel;
                if (vm != null && !string.IsNullOrEmpty(vm.Settings.LocalInstallPath))
                    dialog.SelectedPath = vm.Settings.LocalInstallPath;

                if (dialog.ShowDialog() == DialogResult.OK && vm != null)
                    vm.Settings.LocalInstallPath = dialog.SelectedPath;
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as NasConnectorSettingsViewModel;
            if (vm != null)
                vm.Settings.SmbPassword = PasswordBox.Password;
        }
    }
}
