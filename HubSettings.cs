using System;
using System.IO;
using System.Text.Json;

namespace AvanpostHub
{
    public sealed class HubSettings
    {
        public int Port { get; set; } = 7830;
        public string ScreenerBaseUrl { get; set; } = "https://avanpost.io";
        public bool Autostart { get; set; }
        public bool StartMinimized { get; set; } = true;

        public HubSettings Sanitized()
        {
            var s = Clone();
            if (s.Port < 1 || s.Port > 65535) s.Port = 7830;
            s.ScreenerBaseUrl = (s.ScreenerBaseUrl ?? "").Trim().TrimEnd('/');
            if (s.ScreenerBaseUrl.Length == 0) s.ScreenerBaseUrl = "https://avanpost.io";
            return s;
        }

        public HubSettings Clone()
        {
            return new HubSettings
            {
                Port = Port,
                ScreenerBaseUrl = ScreenerBaseUrl,
                Autostart = Autostart,
                StartMinimized = StartMinimized,
            };
        }
    }

    public static class AppPaths
    {
        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvanpostHub");

        public static string SettingsFile => Path.Combine(Root, "settings.json");
        public static string LogDirectory => Path.Combine(Root, "logs");
        public static string LogFile => Path.Combine(LogDirectory, "avanpost-hub.log");
    }

    public sealed class SettingsStore
    {
        private readonly string _path;

        public SettingsStore()
            : this(AppPaths.SettingsFile)
        {
        }

        public SettingsStore(string path)
        {
            _path = path;
        }

        public string PathOnDisk => _path;
        public bool Exists => File.Exists(_path);

        public HubSettings Load()
        {
            try
            {
                if (!File.Exists(_path)) return new HubSettings();
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<HubSettings>(json, HubJson.Options);
                return (settings ?? new HubSettings()).Sanitized();
            }
            catch (Exception ex)
            {
                HubLog.Error("settings load failed: " + ex.Message);
                return new HubSettings();
            }
        }

        public void Save(HubSettings settings)
        {
            var clean = (settings ?? new HubSettings()).Sanitized();
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(clean, new JsonSerializerOptions(HubJson.Options)
            {
                WriteIndented = true,
            }));
            File.Move(tmp, _path, true);
        }
    }
}
