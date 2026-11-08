using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dubluri.Core.Platform;

namespace Dubluri.Platform.Windows;

/// <summary>
/// Mutare in Recycle Bin prin IFileOperation. Windows poate transforma tacit o "trimitere la cos" in
/// stergere definitiva (retea, stick, fisier prea mare pentru cos); sink-ul de progres anuleaza
/// operatia in acest caz, deci serviciul nu sterge niciodata definitiv.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrashService : ITrashService
{
    private readonly WindowsPathResolver _resolver = new();

    public bool CanTrash(string path, out string? reason)
    {
        reason = null;
        string target = _resolver.ResolveReal(path) ?? _resolver.Normalize(path);
        if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "Unitățile de rețea nu au Recycle Bin. Folosește carantina.";
            return false;
        }
        var type = new DriveInfo(Path.GetPathRoot(target)!).DriveType;
        if (type != DriveType.Fixed)
        {
            reason = type == DriveType.Network
                ? "Unitățile de rețea nu au Recycle Bin. Folosește carantina."
                : "Unitatea aceasta nu are Recycle Bin (stick USB). Folosește carantina.";
            return false;
        }
        return true;
    }

    public void MoveToTrash(string path)
    {
        // IFileOperation functioneaza doar pe un thread STA
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Recycle(path); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    private void Recycle(string path)
    {
        string full = _resolver.Normalize(path);
        int hr = SHCreateItemFromParsingName(full, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem item);
        if (hr != 0) throw new IOException($"Calea nu poate fi trimisă în Recycle Bin (0x{hr:X8}). Folosește carantina.");

        var op = (IFileOperation)new FileOperationCoClass();
        var sink = new RecycleSink();
        op.Advise(sink, out uint cookie);
        try
        {
            op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE);
            op.DeleteItem(item, IntPtr.Zero);
            int perform = op.PerformOperations();
            if (sink.RefusedPermanent)
                throw new IOException("Fișierul nu poate ajunge în Recycle Bin, ar fi fost șters definitiv. Operația a fost anulată; folosește carantina.");
            if (perform < 0) throw new IOException($"Windows a refuzat operația (0x{perform:X8}).");
            if (sink.DeleteResult < 0) throw new IOException($"Windows a refuzat operația (0x{sink.DeleteResult:X8}).");
            if (!sink.Recycled) throw new IOException("Windows nu a confirmat mutarea în Recycle Bin.");
        }
        finally
        {
            op.Unadvise(cookie);
            Marshal.FinalReleaseComObject(op);
            Marshal.FinalReleaseComObject(item);
        }
    }

    // ------------------------------------------------------------------ COM

    private const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400,
                       FOFX_RECYCLEONDELETE = 0x00080000, FOFX_EARLYFAILURE = 0x00100000;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const int E_ABORT = unchecked((int)0x80004004);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid riid, out IShellItem item);

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private class FileOperationCoClass { }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem folder);
        void CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem folder);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
                     [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr newName);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr folder, IntPtr name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr folder, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint sofar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    private sealed class RecycleSink : IFileOperationProgressSink
    {
        public bool RefusedPermanent { get; private set; }
        public bool Recycled { get; private set; }
        public int DeleteResult { get; private set; }

        public int PreDeleteItem(uint flags, IntPtr item)
        {
            if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0) return 0;
            RefusedPermanent = true;
            return E_ABORT;
        }

        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
        {
            DeleteResult = result;
            Recycled = result >= 0 && created != IntPtr.Zero; // HRESULT pozitiv (ex. 0x00270008) inseamna tot succes
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, IntPtr newName) => 0;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr folder, IntPtr name) => 0;
        public int PostNewItem(uint flags, IntPtr folder, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint sofar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
