using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using Forms = System.Windows.Forms;

namespace DropKitWindows
{
    // A separate, hidden message loop keeps F4 available after the clipboard UI exits.
    // It never opens the clipboard or loads history; the normal application owns those.
    public static class F4Launcher
    {
        static string Name { get { return "Local\\DropKitWindows-F4-" + WindowsIdentity.GetCurrent().User.Value; } }
        public static string StartupCommand { get { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --f4-launcher --background"; } }

        public static bool IsReady()
        {
            try { using (var ready = EventWaitHandle.OpenExisting(Name + ".Ready")) return ready.WaitOne(0); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static void EnsureRunning()
        {
            if (IsReady()) return;
            try
            {
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".Ready"))
                {
                    StartProcess("--f4-launcher");
                    ready.WaitOne(2500);
                }
            }
            catch { /* The application can still register its own F4 as a fallback. */ }
        }

        public static void Stop()
        {
            try { using (var stop = EventWaitHandle.OpenExisting(Name + ".Stop")) stop.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        static void StartProcess(string arguments)
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            using (var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe),
                CreateNoWindow = true, WindowStyle = arguments == "--history" || arguments == "--toggle" ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
            })) { }
        }

        public static int Run(bool startBackground)
        {
            using (var singleton = new Mutex(false, Name + ".Mutex"))
            {
                bool owns;
                try { owns = singleton.WaitOne(0); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns)
                {
                    if (startBackground) StartProcess("--background");
                    return 0;
                }
                try
                {
                    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".Ready"))
                    using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".Stop"))
                    using (var window = new HotkeyWindow(ready))
                    using (var timer = new Forms.Timer { Interval = 500 })
                    {
                        stop.Reset();
                        window.Register();
                        if (startBackground) StartProcess("--background");
                        timer.Tick += delegate
                        {
                            if (stop.WaitOne(0)) Forms.Application.ExitThread();
                            else window.Register();
                        };
                        timer.Start();
                        Forms.Application.Run();
                    }
                    return 0;
                }
                finally { singleton.ReleaseMutex(); }
            }
        }

        sealed class HotkeyWindow : Forms.NativeWindow, IDisposable
        {
            readonly EventWaitHandle ready;
            readonly Stopwatch presses = Stopwatch.StartNew();
            bool registered;
            long lastPress = -1000;
            public HotkeyWindow(EventWaitHandle readyEvent)
            {
                ready = readyEvent;
                CreateHandle(new Forms.CreateParams { Caption = "DropKit F4 Launcher", Parent = new IntPtr(-3) });
            }
            public void Register()
            {
                if (registered) return;
                registered = Native.RegisterHotKey(Handle, 14, 0x4000u, 0x73);
                if (registered) ready.Set(); else ready.Reset();
            }
            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == 0x0312 && message.WParam.ToInt32() == 14 && presses.ElapsedMilliseconds - lastPress >= 300)
                {
                    lastPress = presses.ElapsedMilliseconds;
                    try { StartProcess("--toggle"); }
                    catch (Exception ex) { Forms.MessageBox.Show("无法启动 DropKit：" + ex.Message, "DropKit F4"); }
                }
                base.WndProc(ref message);
            }
            public void Dispose()
            {
                ready.Reset();
                if (registered) Native.UnregisterHotKey(Handle, 14);
                DestroyHandle();
            }
        }
    }
}
