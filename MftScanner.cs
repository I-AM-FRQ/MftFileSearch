using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MftFileSearch;

/// <summary>通过 NTFS MFT / USN 接口枚举卷中的文件和目录节点。</summary>
internal sealed class MftScanner : IDisposable
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const int BufferSize = 64 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumData
    {
        public long StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    // USN_RECORD_V2。FSCTL_ENUM_USN_DATA 返回的记录以该布局开始。
    [StructLayout(LayoutKind.Sequential)]
    private struct UsnRecordV2
    {
        public int RecordLength;
        public short MajorVersion;
        public short MinorVersion;
        public long FileReferenceNumber;
        public long ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public FileAttributes FileAttributes;
        public short FileNameLength;
        public short FileNameOffset;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        ref MftEnumData inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _volumeHandle = InvalidHandleValue;
    private IntPtr _buffer = IntPtr.Zero;
    private bool _disposed;

    /// <summary>
    /// 枚举 MFT 节点。完整路径不在扫描时构造，调用方使用 FRN 父子关系按需还原，避免重复保存路径。
    /// </summary>
    internal IEnumerable<MftNodeRecord> EnumerateNodes(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MFT 扫描仅支持 Windows。");
        }

        string driveName = NormalizeDriveName(rootDirectory);
        _volumeHandle = OpenVolume(driveName);
        _buffer = Marshal.AllocHGlobal(BufferSize);

        var enumData = new MftEnumData
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };

        while (true)
        {
            if (!DeviceIoControl(
                    _volumeHandle,
                    FsctlEnumUsnData,
                    ref enumData,
                    Marshal.SizeOf<MftEnumData>(),
                    _buffer,
                    BufferSize,
                    out int bytesReturned,
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                // ERROR_HANDLE_EOF：所有记录都已读取。
                if (error == 38)
                {
                    break;
                }

                throw new Win32Exception(error, $"枚举卷 {driveName} 的 MFT 失败。");
            }

            if (bytesReturned <= sizeof(long))
            {
                break;
            }

            long nextFileReferenceNumber = Marshal.ReadInt64(_buffer);
            int remaining = bytesReturned - sizeof(long);
            IntPtr recordPointer = IntPtr.Add(_buffer, sizeof(long));

            while (remaining > 0)
            {
                if (remaining < Marshal.SizeOf<UsnRecordV2>())
                {
                    throw new InvalidDataException("USN 记录不完整。");
                }

                UsnRecordV2 record = Marshal.PtrToStructure<UsnRecordV2>(recordPointer);
                if (record.RecordLength <= 0 || record.RecordLength > remaining)
                {
                    throw new InvalidDataException("USN 记录长度无效。");
                }

                // V3 使用 128 位文件引用号，布局与此处的 V2 结构不同，不能按 V2 解析。
                if (record.MajorVersion == 2 &&
                    record.FileNameLength >= 0 &&
                    record.FileNameOffset >= 0 &&
                    record.FileNameOffset + record.FileNameLength <= record.RecordLength)
                {
                    string? name = Marshal.PtrToStringUni(
                        IntPtr.Add(recordPointer, record.FileNameOffset),
                        record.FileNameLength / sizeof(char));

                    if (!string.IsNullOrEmpty(name))
                    {
                        yield return new MftNodeRecord(
                            record.FileReferenceNumber,
                            record.ParentFileReferenceNumber,
                            name,
                            (record.FileAttributes & FileAttributes.Directory) == 0);
                    }
                }

                recordPointer = IntPtr.Add(recordPointer, record.RecordLength);
                remaining -= record.RecordLength;
            }

            enumData.StartFileReferenceNumber = nextFileReferenceNumber;
        }

    }

    private static string NormalizeDriveName(string rootDirectory)
    {
        string trimmed = rootDirectory.Trim().TrimEnd('\\', '/');
        if (trimmed.Length != 2 || trimmed[1] != ':')
        {
            throw new ArgumentException("驱动器必须类似 C: 或 C:\\。", nameof(rootDirectory));
        }

        return trimmed.ToUpperInvariant();
    }

    private static IntPtr OpenVolume(string driveName)
    {
        IntPtr handle = CreateFile(
            @"\\.\" + driveName,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开卷 {driveName}。请以管理员身份运行。");
        }

        return handle;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_volumeHandle != InvalidHandleValue && _volumeHandle != IntPtr.Zero)
        {
            CloseHandle(_volumeHandle);
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
        }

        _volumeHandle = InvalidHandleValue;
        _buffer = IntPtr.Zero;
        _disposed = true;
    }
}

/// <summary>用于按父 FRN 关系还原路径的紧凑 MFT 节点记录。</summary>
internal sealed record MftNodeRecord(long Frn, long ParentFrn, string FileName, bool IsFile);
