using System.Windows;
using System.Windows.Controls;
using Thickness = System.Windows.Thickness;

namespace NasConnector
{
    public class NasConnectorSettingsView : System.Windows.Controls.UserControl
    {
        private PasswordBox passwordBox;
        private bool seedingPassword;

        public NasConnectorSettingsView()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var root = new StackPanel { Margin = new Thickness(10) };
            scroll.Content = root;
            Content = scroll;

            // NAS Path
            root.Children.Add(Label("NAS Games Folder (UNC path, e.g. \\\\NAS\\Games):"));
            var nasPathBox = BoundTextBox("Settings.NasBasePath");
            root.Children.Add(nasPathBox);
            AddSpacer(root);

            // Local Install Path
            root.Children.Add(Label("Local Install Directory:"));
            var localPathPanel = new DockPanel { LastChildFill = true };
            var browseBtn = new System.Windows.Controls.Button
            {
                Content = "Browse...", Width = 80, Margin = new Thickness(5, 0, 0, 0)
            };
            // Use Playnite's own folder picker (controller-navigable in Fullscreen)
            // instead of the mouse-only WinForms FolderBrowserDialog.
            browseBtn.SetBinding(System.Windows.Controls.Button.CommandProperty,
                new System.Windows.Data.Binding("BrowseLocalPathCommand"));
            DockPanel.SetDock(browseBtn, Dock.Right);
            localPathPanel.Children.Add(browseBtn);
            localPathPanel.Children.Add(BoundTextBox("Settings.LocalInstallPath"));
            root.Children.Add(localPathPanel);
            AddSpacer(root);

            // Authentication expander
            var authExpander = new Expander
            {
                Header = "Authentication (leave blank to use current Windows user)",
                Margin = new Thickness(0, 0, 0, 10)
            };
            var authPanel = new StackPanel { Margin = new Thickness(10, 5, 0, 0) };
            authPanel.Children.Add(Label("Username:"));
            authPanel.Children.Add(BoundTextBox("Settings.SmbUsername"));
            authPanel.Children.Add(Label("Password:"));
            passwordBox = new PasswordBox();
            passwordBox.PasswordChanged += (s, e) =>
            {
                if (seedingPassword) return; // ignore the programmatic seed below
                var vm = DataContext as NasConnectorSettingsViewModel;
                if (vm != null) vm.Settings.SmbPassword = passwordBox.Password;
            };
            authPanel.Children.Add(passwordBox);

            // DataContext is assigned by GetSettingsView after construction, so seed the
            // PasswordBox once the VM is available (PasswordBox.Password can't be bound).
            DataContextChanged += (s, e) =>
            {
                var vm = DataContext as NasConnectorSettingsViewModel;
                if (vm == null) return;
                seedingPassword = true;
                passwordBox.Password = vm.Settings.SmbPassword ?? string.Empty;
                seedingPassword = false;
            };
            authExpander.Content = authPanel;
            root.Children.Add(authExpander);

            // Excluded folders
            var excludeGroup = new System.Windows.Controls.GroupBox
            {
                Header = "Excluded folders",
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var excludePanel = new StackPanel();
            excludePanel.Children.Add(new TextBlock
            {
                Text = "Folder names to hide (one per line). Use this when a NAS folder duplicates a game you already have installed under a different name.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            var excludeBox = new System.Windows.Controls.TextBox
            {
                AcceptsReturn = true,
                MinHeight = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            excludeBox.SetBinding(System.Windows.Controls.TextBox.TextProperty,
                new System.Windows.Data.Binding("Settings.ExcludedFolders")
                {
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
            excludePanel.Children.Add(excludeBox);
            excludeGroup.Content = excludePanel;
            root.Children.Add(excludeGroup);

            // Test Connection button
            var testBtn = new System.Windows.Controls.Button
            {
                Content = "Test Connection",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4)
            };
            testBtn.SetBinding(System.Windows.Controls.Button.CommandProperty,
                new System.Windows.Data.Binding("TestConnectionCommand"));
            root.Children.Add(testBtn);
        }

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };

        private static void AddSpacer(StackPanel panel) =>
            panel.Children.Add(new System.Windows.Controls.Separator
                { Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Hidden, Height = 2 });

        private static System.Windows.Controls.TextBox BoundTextBox(string path)
        {
            var tb = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10) };
            tb.SetBinding(System.Windows.Controls.TextBox.TextProperty,
                new System.Windows.Data.Binding(path)
                {
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
            return tb;
        }

    }
}
