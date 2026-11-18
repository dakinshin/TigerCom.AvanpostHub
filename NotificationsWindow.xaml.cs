using System;
using System.Windows;
using System.Windows.Media;

namespace AvanpostHub
{
    public partial class NotificationsWindow : Window
    {
        private readonly App _app;
        private readonly HubRuntime _runtime;

        public NotificationsWindow(App app, HubRuntime runtime)
        {
            InitializeComponent();
            SourceInitialized += (_, __) => WindowTheme.UseDarkTitleBar(this);
            _app = app;
            _runtime = runtime;
            LoadSettings(_app.Settings.Notifications ?? new NotificationSettings());
        }

        private void LoadSettings(NotificationSettings settings)
        {
            EnabledCheck.IsChecked = settings.Enabled;
            NtfyUrlBox.Text = string.IsNullOrWhiteSpace(settings.NtfyBaseUrl) ? "https://ntfy.sh" : settings.NtfyBaseUrl;
            NtfyTopicBox.Text = settings.NtfyTopic ?? "";
            NtfyTokenBox.Password = settings.NtfyAccessToken ?? "";
        }

        private NotificationSettings ReadSettings()
        {
            return new NotificationSettings
            {
                Enabled = EnabledCheck.IsChecked == true,
                NtfyBaseUrl = NtfyUrlBox.Text,
                NtfyTopic = NtfyTopicBox.Text,
                NtfyAccessToken = NtfyTokenBox.Password,
            }.Sanitized();
        }

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            var settings = ReadSettings();
            settings.Enabled = true;
            if (string.IsNullOrWhiteSpace(settings.NtfyTopic))
            {
                TestStatusText.Foreground = (Brush)FindResource("DangerBrush");
                TestStatusText.Text = "Укажите топик.";
                return;
            }

            TestButton.IsEnabled = false;
            TestStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            TestStatusText.Text = "Отправляю…";
            try
            {
                var result = await _runtime.Notifications.SendTestAsync(settings);
                if (result.Success)
                {
                    TestStatusText.Foreground = (Brush)FindResource("AccentBrush");
                    TestStatusText.Text = "Тестовое уведомление отправлено.";
                }
                else
                {
                    TestStatusText.Foreground = (Brush)FindResource("DangerBrush");
                    TestStatusText.Text = "Ошибка: " + (result.Error ?? "неизвестная");
                }
            }
            catch (Exception ex)
            {
                TestStatusText.Foreground = (Brush)FindResource("DangerBrush");
                TestStatusText.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var notifications = ReadSettings();
            if (notifications.Enabled && string.IsNullOrWhiteSpace(notifications.NtfyTopic))
            {
                MessageBox.Show("Укажите топик ntfy.", "Уведомления", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var next = _app.Settings.Clone();
            next.Notifications = notifications;
            var error = _app.ApplySettings(next, restart: false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "Уведомления", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
