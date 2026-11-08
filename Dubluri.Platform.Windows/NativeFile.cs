using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dubluri.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class NativeFile
{
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileIdInfo = 18;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_ID_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <summary>Forma \\?\ a unei cai complete, pentru cai peste 260 de caractere.</summary>
    public static string ToExtended(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    /// <summary>Deschide fisierul sau folderul doar pentru atribute, fara sa-l blocheze pentru altii.</summary>
    public static SafeFileHandle Open(string fullPath, out int error)
    {
        var handle = CreateFileW(ToExtended(fullPath), 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    /// <summary>Calea finala (junction, symlink, subst, unitati mapate si nume 8.3 rezolvate); null la eroare.</summary>
    public static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        return length == 0 ? null : buffer.ToString();
    }

    public static (ulong Volume, ulong High, ulong Low)? GetFileId(SafeFileHandle handle)
    {
        if (GetFileInformationByHandleEx(handle, FileIdInfo, out var id, (uint)Marshal.SizeOf<FILE_ID_INFO>())
            && (id.IdLow != 0 || id.IdHigh != 0))
            return (id.VolumeSerialNumber, id.IdHigh, id.IdLow);

        if (GetFileInformationByHandle(handle, out var info) && (info.IndexHigh != 0 || info.IndexLow != 0))
            return (info.VolumeSerialNumber, 0, ((ulong)info.IndexHigh << 32) | info.IndexLow);

        return null;
    }
}
