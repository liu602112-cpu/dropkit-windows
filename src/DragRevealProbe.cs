using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DropKitWindows
{
    // A deliberate shake only starts a brief, invisible OLE target. Ordinary
    // mouse movement cannot produce DragEnter, so it can never reveal the panel.
    public sealed class DragRevealProbe : IDisposable
    {
        readonly Window probe;
        readonly DispatcherTimer expiry;
        readonly Action<Clip> accepted;
        bool matched, disposed;
        public event Action<string> Trace;
        public bool IsChecking { get { return !disposed && probe.IsVisible; } }
        public DragRevealProbe(Action<Clip> onAccepted)
        {
            accepted = onAccepted;
            probe = new Window
            {
                Title = "DropKit drag validation", Width = 24, Height = 24,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, AllowDrop = true,
                Focusable = false
            };
            probe.SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(probe).Handle;
                Native.SetWindowLongPtr(handle, -20, new IntPtr(Native.GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x80L));
            };
            expiry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            expiry.Tick += delegate { Cancel(); };
            probe.PreviewDragEnter += Validate;
            probe.PreviewDragOver += Validate;
            probe.PreviewDrop += delegate(object sender, DragEventArgs e) { e.Effects = DragDropEffects.None; e.Handled = true; Cancel(); };
        }
        public void Begin(Point screenPosition)
        {
            if (disposed || IsChecking) return;
            matched = false;
            new WindowInteropHelper(probe).EnsureHandle();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(probe).Handle);
            var position = source.CompositionTarget.TransformFromDevice.Transform(screenPosition);
            probe.Left = position.X - 12; probe.Top = position.Y - 12;
            probe.Show(); expiry.Start();
            if (Trace != null) Trace("probe " + probe.Left + "," + probe.Top);
        }
        void Validate(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None; e.Handled = true;
            if (Trace != null) Trace("OLE " + e.RoutedEvent.Name + " " + e.KeyStates);
            if (matched || disposed || (e.KeyStates & DragDropKeyStates.LeftMouseButton) == 0) return;
            try
            {
                Clip clip = DropContent.Preview(e.Data, Native.ProcessName(Native.GetForegroundWindow()));
                if (clip == null) return;
                matched = true;
                // The source's nested OLE loop can defer dispatcher work until
                // mouse-up. Notify while this genuine DragEnter is still active;
                // defer only hiding the temporary target to avoid reentrancy.
                accepted(clip);
                probe.Dispatcher.BeginInvoke(new Action(Cancel), DispatcherPriority.Send);
            }
            catch { Cancel(); }
        }
        public void Cancel()
        {
            expiry.Stop();
            if (!disposed && probe.IsVisible) probe.Hide();
        }
        public void Dispose()
        {
            if (disposed) return;
            Cancel(); disposed = true; probe.Close();
        }
    }
}
