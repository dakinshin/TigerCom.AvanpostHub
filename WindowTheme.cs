using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvanpostHub
{
    internal static class WindowTheme
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        public static void UseDarkTitleBar(Window window)
        {
            if (window == null) return;
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var enabled = 1;
                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode,
                        ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1,
                        ref enabled, sizeof(int));
                }
            }
            catch
            {
                // Старые версии Windows могут не поддерживать тёмный title bar.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int valueSize);
    }
}
