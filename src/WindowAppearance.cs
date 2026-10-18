using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace DropKitWindows
{
    public static class WindowAppearance
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void Configure(Window window)
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false
            });
        }
        public static bool Apply(Window window, HwndSource source)
        {
            bool acrylic = false;
            try
            {
                int light = 0, rounded = 2, backdrop = 3;
                DwmSetWindowAttribute(source.Handle, 20, ref light, 4);
                DwmSetWindowAttribute(source.Handle, 33, ref rounded, 4);
                acrylic = DwmSetWindowAttribute(source.Handle, 38, ref backdrop, 4) == 0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            window.Background = acrylic ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(239, 243, 249));
            source.CompositionTarget.BackgroundColor = acrylic ? Colors.Transparent : Color.FromRgb(239, 243, 249);
            var surface = window.FindName("GlassSurface") as System.Windows.Controls.Border;
            if (surface != null) surface.Background = new SolidColorBrush(Color.FromArgb(acrylic ? (byte)92 : (byte)255, 242, 246, 252));
            return acrylic;
        }
        public static Rect BottomRight(Rect workArea, Size requested, double gap)
        {
            double width = Math.Min(requested.Width, Math.Max(1, workArea.Width - 2 * gap));
            double height = Math.Min(requested.Height, Math.Max(1, workArea.Height - 2 * gap));
            return new Rect(Math.Max(workArea.Left, workArea.Right - width - gap), Math.Max(workArea.Top, workArea.Bottom - height - gap), width, height);
        }
    }
}
