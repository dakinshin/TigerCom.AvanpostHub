using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AvanpostHub
{
    public partial class MainWindow : Window
    {
        private readonly HubRuntime _runtime;
        private readonly App _app;
        private readonly DispatcherTimer _statusTimer;

        public MainWindow(HubRuntime runtime, App app)
        {
            InitializeComponent();
            _runtime = runtime;
            _app = app;

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (_, __) => UpdateStatus();
            _statusTimer.Start();
            UpdateStatus();
        }

        public void LoadSettings(HubSettings settings)
        {
            if (settings == null) return;
            PortBox.Text = settings.Port.ToString();
            AutostartCheck.IsChecked = settings.Autostart;
            StartMinimizedCheck.IsChecked = settings.StartMinimized;
        }

        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Focus();
        }

        public void UpdateStatus()
        {
            var status = _runtime.GetStatus();
            var running = string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase);
            StatusText.Text = running ? "работает" : "остановлен";
            StatusText.Foreground = running ? Brushes.SeaGreen : Brushes.IndianRed;
            EndpointText.Text = status.Endpoint;
            ClientsText.Text = status.Clients + " клиентов · " + status.Subscriptions + " подписок";
            FeatureText.Text = status.FeatureSeries + " серий · " + status.FeatureBars + " закрытых баров";
            UptimeText.Text = status.UptimeMs <= 0
                ? "—"
                : TimeSpan.FromMilliseconds(status.UptimeMs).ToString(@"hh\:mm\:ss");
            ErrorText.Text = string.IsNullOrWhiteSpace(status.LastError)
                ? "Хаб слушает только 127.0.0.1. Коннекторы бирж будут добавлены следующим этапом."
                : status.LastError;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            int port;
            if (!int.TryParse(PortBox.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Порт должен быть числом от 1 до 65535.", "Avanpost Hub",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var next = new HubSettings
            {
                Port = port,
                Autostart = AutostartCheck.IsChecked == true,
                StartMinimized = StartMinimizedCheck.IsChecked == true,
            };

            var error = _app.ApplySettings(next);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "Avanpost Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateStatus();
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            var error = _runtime.Restart(_app.Settings);
            if (!string.IsNullOrWhiteSpace(error))
                MessageBox.Show(error, "Avanpost Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus();
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.LogDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Avanpost Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Hide_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_app.IsExiting) return;
            e.Cancel = true;
            Hide();
        }
    }
}
