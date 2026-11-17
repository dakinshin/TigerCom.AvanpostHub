using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AvanpostHub
{
    public partial class App : Application
    {
        private Mutex _instanceMutex;
        private SettingsStore _settingsStore;
        private HubSettings _settings;
        private HubRuntime _runtime;
        private AutostartManager _autostart;
        private MainWindow _mainWindow;
        private TrayIconController _tray;
        private bool _exiting;

        public bool IsExiting => _exiting;
        public HubRuntime Runtime => _runtime;
        public HubSettings Settings => _settings;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool createdNew;
            _instanceMutex = new Mutex(true, @"Local\AvanpostHub", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Avanpost Hub уже запущен. Откройте его из системного трея.",
                    "Avanpost Hub",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                try { _instanceMutex.Dispose(); } catch { }
                _instanceMutex = null;
                Shutdown();
                return;
            }

            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();
            _autostart = new AutostartManager();
            _runtime = new HubRuntime();
            _mainWindow = new MainWindow(_runtime, this);
            _tray = new TrayIconController(
                ShowMainWindow,
                ExitApplication,
                () => _settings.Autostart,
                SetAutostartFromTray);

            _mainWindow.LoadSettings(_settings);
            _tray.RefreshAutostart();

            var startError = _runtime.Restart(_settings);
            _tray.UpdateStatus(_runtime.GetStatus());
            _mainWindow.UpdateStatus();
            if (!string.IsNullOrWhiteSpace(startError))
            {
                HubLog.Error("hub startup error: " + startError);
                _mainWindow.Show();
            }
            else
            {
                var minimized = e.Args.Any(x => string.Equals(x, "--minimized", StringComparison.OrdinalIgnoreCase)) ||
                                _settings.StartMinimized;
                if (!minimized || !_settingsStore.Exists)
                    _mainWindow.Show();
            }

            if (_settings.Autostart)
            {
                var sync = _autostart.SetEnabled(true);
                if (!sync.Success)
                    HubLog.Warn("autostart sync failed: " + sync.Error);
            }
        }

        public string ApplySettings(HubSettings next, bool restart = true)
        {
            next = (next ?? new HubSettings()).Sanitized();

            var autostart = _autostart.SetEnabled(next.Autostart);
            if (!autostart.Success)
                return autostart.Error;

            _settings = next;
            try { _settingsStore.Save(next); }
            catch (Exception ex)
            {
                HubLog.Error("settings save failed: " + ex.Message);
                return ex.Message;
            }

            var error = restart ? _runtime.Restart(next) : null;
            _mainWindow.LoadSettings(_settings);
            _tray.RefreshAutostart();
            _tray.UpdateStatus(_runtime.GetStatus());
            return error;
        }

        public void SetAutostartFromTray(bool enabled)
        {
            var next = _settings.Clone();
            next.Autostart = enabled;
            var error = ApplySettings(next, restart: false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "Автозапуск", MessageBoxButton.OK, MessageBoxImage.Warning);
                _tray.RefreshAutostart();
                return;
            }

            _mainWindow.LoadSettings(_settings);
            _tray.RefreshAutostart();
        }

        public void ShowMainWindow()
        {
            if (_mainWindow == null) return;
            _mainWindow.ShowFromTray();
        }

        public void ExitApplication()
        {
            if (_exiting) return;
            _exiting = true;
            try { _tray?.Dispose(); } catch { }
            try { _runtime?.Stop(); } catch { }
            try { _mainWindow?.Close(); } catch { }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _tray?.Dispose(); } catch { }
            try { _runtime?.Dispose(); } catch { }
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            try { _instanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
