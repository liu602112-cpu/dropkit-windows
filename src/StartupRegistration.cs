using System;
using Microsoft.Win32;

namespace DropKitWindows
{
    // The OS registration is the source of truth. Do not cache an unchecked UI
    // value and write it later when saving unrelated clipboard preferences.
    public sealed class StartupRegistration
    {
        readonly string runPath, approvalPath, command;
        const string ValueName = "DropKitWindows";
        public StartupRegistration() : this("Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", F4Launcher.StartupCommand) { }
        public StartupRegistration(string run, string approval, string startupCommand)
        { runPath = run; approvalPath = approval; command = startupCommand; }
        public bool Enabled
        {
            get
            {
                using (var run = Registry.CurrentUser.OpenSubKey(runPath))
                    if (run == null || !string.Equals(run.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase)) return false;
                using (var approved = Registry.CurrentUser.OpenSubKey(approvalPath))
                {
                    var flags = approved == null ? null : approved.GetValue(ValueName) as byte[];
                    return flags == null || flags.Length == 0 || (flags[0] & 1) == 0;
                }
            }
        }
        public void SetEnabled(bool enabled)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(runPath))
            {
                if (enabled) run.SetValue(ValueName, command, RegistryValueKind.String);
                else run.DeleteValue(ValueName, false);
                run.Flush();
            }
            // An explicit click on Enable also clears this app's prior disabled
            // startup state. Never change other applications' startup entries.
            if (enabled)
                using (var approved = Registry.CurrentUser.OpenSubKey(approvalPath, true))
                    if (approved != null) { approved.DeleteValue(ValueName, false); approved.Flush(); }
            if (Enabled != enabled) throw new InvalidOperationException("Windows 未保留自启设置，请重新尝试。");
        }
    }
}
