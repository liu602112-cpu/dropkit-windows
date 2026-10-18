using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DropKitWindows
{
    public static class RecycleShell
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);
        public static string MoveToBin(string path)
        {
            path = Path.GetFullPath(path);
            string root = Path.GetPathRoot(path);
            if (path.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || new DriveInfo(root).DriveType != DriveType.Fixed)
                throw new IOException("此位置不能保证移入回收站，文件已保留。");
            IShellItem item; Guid iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09")));
            var sink = new RecycleSink();
            try
            {
                // Windows 8+ force-recycle flag; never request a permanent delete.
                operation.SetOperationFlags(0x00080000u | 0x00000040u | 0x00000010u | 0x00000004u | 0x00000400u | 0x00002000u);
                operation.DeleteItem(item, sink); operation.PerformOperations();
                bool aborted; operation.GetAnyOperationsAborted(out aborted);
                if (aborted || sink.Error < 0 || string.IsNullOrEmpty(sink.RecycledPath)) throw new IOException("未能移入回收站，记录已保留。", sink.Error < 0 ? Marshal.GetExceptionForHR(sink.Error) : null);
                return sink.RecycledPath;
            }
            finally { Marshal.FinalReleaseComObject(item); Marshal.FinalReleaseComObject(operation); }
        }
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr bind, ref Guid bhid, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hints, out int order);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object dialog);
        void SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        void SetOwnerWindow(uint hwnd);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleSink : IFileOperationProgressSink
    {
        public int Error;
        public string RecycledPath;
        public int StartOperations() { return 0; } public int FinishOperations(int result) { if (result < 0) Error = result; return 0; }
        public int PreRenameItem(uint f, IShellItem i, string n) { return 0; } public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem c) { return 0; }
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) { return 0; } public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem c) { return 0; }
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) { return 0; } public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem c) { return 0; }
        public int PreDeleteItem(uint f, IShellItem i) { return (f & 0x80u) != 0 ? 0 : unchecked((int)0x80004004); }
        public int PostDeleteItem(uint f, IShellItem i, int r, IShellItem c)
        {
            Error = r;
            if (r >= 0 && c != null)
            {
                IntPtr value; c.GetDisplayName(0x80058000u, out value);
                try { RecycledPath = Marshal.PtrToStringUni(value); } finally { Marshal.FreeCoTaskMem(value); }
            }
            return 0;
        }
        public int PreNewItem(uint f, IShellItem d, string n) { return 0; } public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int r, IShellItem c) { return 0; }
        public int UpdateProgress(uint t, uint d) { return 0; } public int ResetTimer() { return 0; } public int PauseTimer() { return 0; } public int ResumeTimer() { return 0; }
    }
}
