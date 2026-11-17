using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace AvanpostHub
{
    public sealed class AutostartResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }

        public static AutostartResult Ok()
        {
            return new AutostartResult { Success = true };
        }

        public static AutostartResult Fail(string error)
        {
            return new AutostartResult { Success = false, Error = error };
        }
    }

    public sealed class AutostartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AvanpostHub";

        public string LaunchCommand => BuildLaunchCommand();

        public bool IsEnabled()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var value = key == null ? null : key.GetValue(ValueName) as string;
                    return string.Equals(value, LaunchCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                HubLog.Warn("autostart read failed: " + ex.Message);
                return false;
            }
        }

        public AutostartResult SetEnabled(bool enabled)
        {
            if (!OperatingSystem.IsWindows())
                return AutostartResult.Fail("Автозапуск доступен только в Windows.");

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return AutostartResult.Fail("Не удалось открыть ключ автозапуска HKCU.");

                    if (enabled)
                    {
                        var command = LaunchCommand;
                        if (string.IsNullOrWhiteSpace(command))
                            return AutostartResult.Fail("Не удалось определить путь к AvanpostHub.exe.");
                        key.SetValue(ValueName, command, RegistryValueKind.String);
                        HubLog.Info("autostart enabled: " + LaunchCommand);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                        HubLog.Info("autostart disabled");
                    }
                }

                return AutostartResult.Ok();
            }
            catch (Exception ex)
            {
                HubLog.Error("autostart write failed: " + ex.Message);
                return AutostartResult.Fail(ex.Message);
            }
        }

        private static string BuildLaunchCommand()
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                processPath = Assembly.GetEntryAssembly()?.Location ?? "";

            if (string.IsNullOrWhiteSpace(processPath)) return "";
            var fileName = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entry = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrWhiteSpace(entry))
                    return Quote(processPath) + " " + Quote(entry) + " --minimized";
            }

            return Quote(processPath) + " --minimized";
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "") + "\"";
        }
    }
}
