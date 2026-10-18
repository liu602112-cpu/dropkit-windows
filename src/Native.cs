using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DropKitWindows
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr window, int index, int value);
        public static IntPtr GetWindowLongPtr(IntPtr window, int index) { return IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index)); }
        public static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) { return IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32())); }
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd, string name);
        delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        public const string MainWindowMarker = "DropKitWindows.MainWindow";
        public static bool AllowExistingActivation()
        {
            bool found = false;
            EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
            {
                if (GetProp(hwnd, MainWindowMarker) == IntPtr.Zero) return true;
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)Process.GetCurrentProcess().Id) return true;
                AllowSetForegroundWindow(pid); found = true; return false;
            }, IntPtr.Zero);
            return found;
        }
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT key;
        }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        public static string ProcessName(IntPtr window)
        {
            try { uint id; GetWindowThreadProcessId(window, out id); return Process.GetProcessById((int)id).ProcessName; } catch { return ""; }
        }
        public static bool OwnWindow(IntPtr window)
        {
            uint id; GetWindowThreadProcessId(window, out id); return id == (uint)Process.GetCurrentProcess().Id;
        }
        public static bool Paste()
        {
            var inputs = new INPUT[4];
            ushort[] keys = { 0x11, 0x56, 0x56, 0x11 };
            for (int i = 0; i < 4; i++) { inputs[i].type = 1; inputs[i].U.key.vk = keys[i]; inputs[i].U.key.flags = i >= 2 ? 2u : 0u; }
            return SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT))) == 4;
        }
    }
}
