using System;
using System.Drawing;
using System.Windows.Forms;

namespace AvanpostHub
{
    public sealed class TrayIconController : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly Func<bool> _isAutostart;
        private readonly Action<bool> _setAutostart;
        private readonly Icon _appIcon;
        private readonly bool _ownsAppIcon;
        private bool _disposed;

        public TrayIconController(
            Action show,
            Action exit,
            Func<bool> isAutostart,
            Action<bool> setAutostart)
        {
            _isAutostart = isAutostart;
            _setAutostart = setAutostart;

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;

            var open = new ToolStripMenuItem("Открыть Avanpost Hub");
            open.Click += (_, __) => show();
            _menu.Items.Add(open);

            _statusItem = new ToolStripMenuItem("Состояние: запуск");
            _statusItem.Enabled = false;
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());

            _autostartItem = new ToolStripMenuItem("Запускать при входе в Windows");
            _autostartItem.CheckOnClick = false;
            _autostartItem.Click += (_, __) =>
            {
                var next = !_isAutostart();
                _setAutostart(next);
                RefreshAutostart();
            };
            _menu.Items.Add(_autostartItem);
            _menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (_, __) => exit();
            _menu.Items.Add(exitItem);

            _appIcon = LoadAppIcon(out _ownsAppIcon);
            _icon = new NotifyIcon
            {
                Icon = _appIcon,
                Text = "Avanpost Hub",
                Visible = true,
                ContextMenuStrip = _menu,
            };
            _icon.DoubleClick += (_, __) => show();
        }

        public void RefreshAutostart()
        {
            if (_disposed) return;
            try { _autostartItem.Checked = _isAutostart(); }
            catch { _autostartItem.Checked = false; }
        }

        public void UpdateStatus(HubStatus status)
        {
            if (_disposed) return;
            var running = status != null && string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase);
            _statusItem.Text = running ? "Состояние: работает" : "Состояние: остановлен";
            _icon.Text = running ? "Avanpost Hub — работает" : "Avanpost Hub — остановлен";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Dispose();
            if (_ownsAppIcon) _appIcon.Dispose();
            _menu.Dispose();
        }

        private static Icon LoadAppIcon(out bool ownsIcon)
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/avanpost.ico", UriKind.Absolute));
                if (resource != null && resource.Stream != null)
                {
                    ownsIcon = true;
                    return new Icon(resource.Stream);
                }
            }
            catch
            {
                // Иконка не должна мешать запуску Hub.
            }

            ownsIcon = false;
            return SystemIcons.Application;
        }
    }
}
