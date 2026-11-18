using System;
using System.Windows;

namespace AvanpostHub
{
    public partial class SettingsWindow : Window
    {
        private readonly App _app;

        public SettingsWindow(App app)
        {
            InitializeComponent();
            SourceInitialized += (_, __) => WindowTheme.UseDarkTitleBar(this);
            _app = app;
            LoadSettings(_app.Settings);
        }

        private void LoadSettings(HubSettings settings)
        {
            if (settings == null) return;
            PortBox.Text = settings.Port.ToString();
            ScreenerUrlBox.Text = settings.ScreenerBaseUrl ?? "";
            AutostartCheck.IsChecked = settings.Autostart;
            StartMinimizedCheck.IsChecked = settings.StartMinimized;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            int port;
            if (!int.TryParse(PortBox.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Порт должен быть числом от 1 до 65535.", "Настройки Avanpost Hub",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var next = _app.Settings.Clone();
            next.Port = port;
            next.ScreenerBaseUrl = ScreenerUrlBox.Text.Trim();
            next.Autostart = AutostartCheck.IsChecked == true;
            next.StartMinimized = StartMinimizedCheck.IsChecked == true;

            var error = _app.ApplySettings(next, restart: true);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "Настройки Avanpost Hub",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
