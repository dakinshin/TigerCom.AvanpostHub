using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AvanpostHub
{
    public partial class MainWindow : Window
    {
        private static readonly Brush OnlineBrush = CreateBrush("#34D399");
        private static readonly Brush WarningBrush = CreateBrush("#FBBF24");
        private static readonly Brush OfflineBrush = CreateBrush("#F87171");
        private static readonly Brush OnlinePillBrush = CreateBrush("#143326");
        private static readonly Brush OnlinePillBorderBrush = CreateBrush("#27624A");
        private static readonly Brush OfflinePillBrush = CreateBrush("#3A1D24");
        private static readonly Brush OfflinePillBorderBrush = CreateBrush("#67313B");
        private static readonly Brush OnlineTextBrush = CreateBrush("#A7F3D0");
        private static readonly Brush OfflineTextBrush = CreateBrush("#FCA5A5");

        private readonly HubRuntime _runtime;
        private readonly App _app;
        private readonly DispatcherTimer _statusTimer;

        public MainWindow(HubRuntime runtime, App app)
        {
            InitializeComponent();
            SourceInitialized += (_, __) => WindowTheme.UseDarkTitleBar(this);
            _runtime = runtime;
            _app = app;
            FitToWorkArea();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (_, __) => UpdateStatus();
            _statusTimer.Start();
            UpdateStatus();
        }

        public void RefreshDashboard()
        {
            UpdateStatus();
        }

        private void FitToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            MaxWidth = area.Width;
            MaxHeight = area.Height;
            if (Width > area.Width - 36) Width = Math.Max(MinWidth, area.Width - 36);
            if (Height > area.Height - 36) Height = Math.Max(MinHeight, area.Height - 36);
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
            var indicatorsOnline = running && status.Clients > 0;
            var screenerOnline = running && status.CalibrationAvailable;
            var exchangesOnline = running && status.Connectors > 0;

            HeaderStatusPill.Background = running ? OnlinePillBrush : OfflinePillBrush;
            HeaderStatusPill.BorderBrush = running ? OnlinePillBorderBrush : OfflinePillBorderBrush;
            HeaderStatusDot.Fill = running ? OnlineBrush : OfflineBrush;
            HeaderStatusText.Foreground = running ? OnlineTextBrush : OfflineTextBrush;
            HeaderStatusText.Text = running ? "Hub работает" : "Hub остановлен";

            StatusMetricText.Text = running ? "ONLINE" : "OFFLINE";
            StatusMetricText.Foreground = running ? OnlineBrush : OfflineBrush;
            StatusMetricDetail.Text = running ? "локальный сервер запущен" : "локальный сервер остановлен";
            ClientsMetricText.Text = status.Clients.ToString();
            ClientsMetricDetail.Text = status.Clients + " клиентов · " + status.Subscriptions + " подписок";
            DataMetricText.Text = status.FeatureBars.ToString("N0");
            DataMetricDetail.Text = status.FeatureSeries + " серий · " + status.Connectors + " коннекторов";
            UptimeMetricText.Text = status.UptimeMs <= 0
                ? "—"
                : TimeSpan.FromMilliseconds(status.UptimeMs).ToString(@"hh\:mm\:ss");
            UptimeMetricDetail.Text = "версия " + HubVersion.Current;

            HubNodeDetailText.Text = string.IsNullOrWhiteSpace(status.Endpoint)
                ? "ws://127.0.0.1:" + status.Port + "/ws"
                : status.Endpoint;
            HubNodeDot.Fill = running ? OnlineBrush : OfflineBrush;
            HubNodeStatusText.Text = running ? "Сервер работает" : "Сервер остановлен";
            HubNodeStatusText.Foreground = running ? OnlineTextBrush : OfflineTextBrush;

            IndicatorNodeDetailText.Text = status.Clients + " клиентов · " + status.Subscriptions + " подписок";
            IndicatorNodeDot.Fill = indicatorsOnline ? OnlineBrush : (running ? WarningBrush : OfflineBrush);
            SetLink(IndicatorLink, IndicatorLinkDot,
                indicatorsOnline ? OnlineBrush : (running ? WarningBrush : OfflineBrush));

            ScreenerNodeStatusText.Text = !running
                ? "Hub остановлен"
                : (status.CalibrationAvailable ? "калибровка загружена" : "ожидание калибровки");
            ScreenerNodeDot.Fill = screenerOnline ? OnlineBrush : (running ? WarningBrush : OfflineBrush);
            SetLink(ScreenerLink, ScreenerLinkDot,
                screenerOnline ? OnlineBrush : (running ? WarningBrush : OfflineBrush));

            ExchangeNodeStatusText.Text = !running
                ? "Hub остановлен"
                : (exchangesOnline ? "Binance подключён" : "нет активных подписок");
            ExchangeNodeDetailText.Text = status.Connectors + " активных коннекторов";
            ExchangeNodeDot.Fill = exchangesOnline ? OnlineBrush : (running ? WarningBrush : OfflineBrush);
            SetLink(ExchangeLink, ExchangeLinkDot,
                exchangesOnline ? OnlineBrush : (running ? WarningBrush : OfflineBrush));

            EndpointText.Text = string.IsNullOrWhiteSpace(status.Endpoint) ? "—" : status.Endpoint;
            StorageText.Text = status.StoredOiPoints.ToString("N0") + " OI-точек · " +
                               status.StoredFeatureBars.ToString("N0") + " final-баров";
            CalibrationText.Text = status.CalibrationAvailable
                ? "OiFlow · " + DateTimeOffset.FromUnixTimeMilliseconds(status.CalibrationGeneratedAtMs)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "профиль OiFlow не загружен";
            if (!status.NotificationsEnabled)
                NotificationsText.Text = "ntfy выключен";
            else if (!status.NotificationsConfigured)
                NotificationsText.Text = "нужны адрес ntfy и топик";
            else
                NotificationsText.Text = "принято " + status.NotificationsQueued +
                                         " · отправлено " + status.NotificationsSent +
                                         " · ошибок " + status.NotificationsFailed +
                                         (string.IsNullOrWhiteSpace(status.NotificationLastError)
                                             ? ""
                                             : " · " + status.NotificationLastError);

            if (string.IsNullOrWhiteSpace(status.LastError))
            {
                ErrorBorder.Visibility = Visibility.Collapsed;
                ErrorText.Text = "";
            }
            else
            {
                ErrorBorder.Visibility = Visibility.Visible;
                ErrorText.Text = status.LastError;
            }

            VersionText.Text = "Avanpost Hub · " + HubVersion.Current;
        }

        private static void SetLink(Shape line, Ellipse dot, Brush brush)
        {
            line.Stroke = brush;
            line.Opacity = 0.9;
            dot.Fill = brush;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow(_app) { Owner = this };
            if (window.ShowDialog() == true) UpdateStatus();
        }

        private void Notifications_Click(object sender, RoutedEventArgs e)
        {
            var window = new NotificationsWindow(_app, _runtime) { Owner = this };
            window.ShowDialog();
            UpdateStatus();
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            var error = _runtime.Restart(_app.Settings);
            if (!string.IsNullOrWhiteSpace(error))
                MessageBox.Show(error, "Avanpost Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            _app.RefreshTrayStatus();
            UpdateStatus();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
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

        private static Brush CreateBrush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }
    }
}
