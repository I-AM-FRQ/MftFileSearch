using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MftFileSearch;

/// <summary>
/// 通过 NTFS 文件引用号（FRN）打开实际文件，再由系统返回其当前路径。
/// 这比由索引中的父目录 FRN 链拼接路径更可靠，可正确处理移动、重命名和已删除的旧记录。
/// </summary>
internal sealed class NtfsFileIdPathResolver : IDisposable
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0x00000000;
    private const uint FileNameNormalized = 0x00000000;

    private enum FileIdType : uint
    {
        FileIdType = 0
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public FileIdType Type;

        [FieldOffset(8)]
        public long FileId;

        // FILE_ID_DESCRIPTOR 的原生 union 最大成员是 GUID（16 字节），用于保证结构大小为 24 字节。
        [FieldOffset(8)]
        public Guid ObjectId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenFileById(
        IntPtr volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _volumeHandle;
    private bool _disposed;

    internal NtfsFileIdPathResolver(string driveName)
    {
        string root = FileIndexDatabase.NormalizeDrive(driveName);
        _volumeHandle = CreateFile(
            @"\\.\" + root,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (_volumeHandle == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开卷 {root} 以确认文件路径。请以管理员身份运行。");
        }
    }

    /// <summary>返回该 FRN 当前存在时的真实 DOS 路径；文件已删除或无法打开时返回 null。</summary>
    internal string? TryGetCurrentPath(long fileReferenceNumber)
    {
        var descriptor = new FileIdDescriptor
        {
            Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
            Type = FileIdType.FileIdType,
            FileId = fileReferenceNumber
        };

        IntPtr fileHandle = OpenFileById(
            _volumeHandle,
            ref descriptor,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics);
        if (fileHandle == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            const uint initialCapacity = 1024;
            var buffer = new StringBuilder((int)initialCapacity);
            uint result = GetFinalPathNameByHandle(
                fileHandle,
                buffer,
                initialCapacity,
                VolumeNameDos | FileNameNormalized);
            if (result == 0)
            {
                return null;
            }

            if (result >= initialCapacity)
            {
                uint capacity = result + 1;
                buffer = new StringBuilder((int)capacity);
                result = GetFinalPathNameByHandle(
                    fileHandle,
                    buffer,
                    capacity,
                    VolumeNameDos | FileNameNormalized);
                if (result == 0 || result >= capacity)
                {
                    return null;
                }
            }

            string path = buffer.ToString();
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        finally
        {
            CloseHandle(fileHandle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_volumeHandle != IntPtr.Zero && _volumeHandle != InvalidHandleValue)
        {
            CloseHandle(_volumeHandle);
        }

        _volumeHandle = InvalidHandleValue;
        _disposed = true;
    }
}
