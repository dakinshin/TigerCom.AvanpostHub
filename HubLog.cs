using System;
using System.IO;
using System.Text;

namespace AvanpostHub
{
    public static class HubLog
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 5L * 1024 * 1024;

        public static string LogFile => AppPaths.LogFile;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    var file = AppPaths.LogFile;
                    if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    {
                        var old = file + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }

                    var line = string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                        DateTime.Now,
                        level,
                        message ?? "",
                        Environment.NewLine);
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Логирование не должно ронять хаб.
            }
        }
    }
}
