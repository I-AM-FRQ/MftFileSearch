using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MftFileSearch;

/// <summary>读取 NTFS USN Change Journal，用于在两次全量扫描之间获取文件系统变更。</summary>
internal sealed class UsnJournalReader : IDisposable
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint AllReasons = 0xFFFFFFFF;
    private const int BufferSize = 64 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

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
        IntPtr inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        ref ReadUsnJournalDataV0 inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly IntPtr _volumeHandle;
    private readonly IntPtr _buffer;
    private bool _disposed;

    internal UsnJournalReader(string driveName)
    {
        string root = NormalizeDrive(driveName);
        _volumeHandle = CreateFile(
            @"\\.\" + root,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (_volumeHandle == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开卷 {root}。请以管理员身份运行。");
        }

        _buffer = Marshal.AllocHGlobal(BufferSize);
    }

    internal UsnJournalState GetState()
    {
        int size = Marshal.SizeOf<UsnJournalDataV0>();
        IntPtr output = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(
                    _volumeHandle,
                    FsctlQueryUsnJournal,
                    IntPtr.Zero,
                    0,
                    output,
                    size,
                    out int bytesReturned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "查询 NTFS USN Journal 失败。");
            }

            if (bytesReturned < size)
            {
                throw new InvalidDataException("USN Journal 状态数据不完整。");
            }

            UsnJournalDataV0 state = Marshal.PtrToStructure<UsnJournalDataV0>(output);
            return new UsnJournalState(state.UsnJournalId, state.FirstUsn, state.NextUsn, state.LowestValidUsn);
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    /// <summary>读取从 <paramref name="startUsn"/> 开始且不晚于当前 Journal 末尾的所有变更。</summary>
    internal UsnJournalReadResult ReadChanges(long startUsn, Action<UsnChangeRecord> onRecord)
    {
        UsnJournalState initialState = GetState();
        if (startUsn < initialState.FirstUsn || startUsn < initialState.LowestValidUsn || startUsn > initialState.NextUsn)
        {
            return UsnJournalReadResult.JournalRangeUnavailable(initialState);
        }

        long nextUsn = startUsn;
        int count = 0;
        while (nextUsn < initialState.NextUsn)
        {
            var input = new ReadUsnJournalDataV0
            {
                StartUsn = nextUsn,
                ReasonMask = AllReasons,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = initialState.JournalId
            };

            if (!DeviceIoControl(
                    _volumeHandle,
                    FsctlReadUsnJournal,
                    ref input,
                    Marshal.SizeOf<ReadUsnJournalDataV0>(),
                    _buffer,
                    BufferSize,
                    out int bytesReturned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 NTFS USN Journal 失败。");
            }

            if (bytesReturned <= sizeof(long))
            {
                break;
            }

            long returnedNextUsn = Marshal.ReadInt64(_buffer);
            if (returnedNextUsn <= nextUsn)
            {
                throw new InvalidDataException("USN Journal 未返回有效的下一读取位置。");
            }

            int remaining = bytesReturned - sizeof(long);
            IntPtr recordPointer = IntPtr.Add(_buffer, sizeof(long));
            while (remaining > 0)
            {
                if (remaining < Marshal.SizeOf<UsnRecordV2>())
                {
                    throw new InvalidDataException("USN Journal 记录不完整。");
                }

                UsnRecordV2 record = Marshal.PtrToStructure<UsnRecordV2>(recordPointer);
                if (record.RecordLength <= 0 || record.RecordLength > remaining)
                {
                    throw new InvalidDataException("USN Journal 记录长度无效。");
                }

                if (TryReadRecord(recordPointer, record.RecordLength, record.MajorVersion, out UsnChangeRecord change))
                {
                    onRecord(change);
                    count++;
                }

                recordPointer = IntPtr.Add(recordPointer, record.RecordLength);
                remaining -= record.RecordLength;
            }

            nextUsn = returnedNextUsn;
        }

        // 读取期间新产生的事件留给下一次 update；提交开始时的边界，避免跳过它们。
        UsnJournalState finalState = GetState();
        if (finalState.JournalId != initialState.JournalId)
        {
            return UsnJournalReadResult.JournalRangeUnavailable(finalState);
        }

        return new UsnJournalReadResult(false, initialState, count);
    }

    private static bool TryReadRecord(IntPtr pointer, int recordLength, short majorVersion, out UsnChangeRecord change)
    {
        change = null!;
        long frn;
        long parentFrn;
        uint reason;
        FileAttributes attributes;
        short nameLength;
        short nameOffset;
        switch (majorVersion)
        {
            case 2:
                if (recordLength < Marshal.SizeOf<UsnRecordV2>())
                {
                    return false;
                }

                UsnRecordV2 v2 = Marshal.PtrToStructure<UsnRecordV2>(pointer);
                frn = v2.FileReferenceNumber;
                parentFrn = v2.ParentFileReferenceNumber;
                reason = v2.Reason;
                attributes = v2.FileAttributes;
                nameLength = v2.FileNameLength;
                nameOffset = v2.FileNameOffset;
                break;
            case 3:
                // USN_RECORD_V3 stores 128-bit file IDs. NTFS volumes using this service retain
                // the low 64 bits used by OpenFileById; the layout otherwise matches V2 after IDs.
                const int V3FileNameLengthOffset = 72;
                const int V3FileNameOffsetOffset = 74;
                if (recordLength < 76)
                {
                    return false;
                }

                frn = Marshal.ReadInt64(pointer, 8);
                parentFrn = Marshal.ReadInt64(pointer, 24);
                reason = unchecked((uint)Marshal.ReadInt32(pointer, 56));
                attributes = (FileAttributes)Marshal.ReadInt32(pointer, 68);
                nameLength = Marshal.ReadInt16(pointer, V3FileNameLengthOffset);
                nameOffset = Marshal.ReadInt16(pointer, V3FileNameOffsetOffset);
                break;
            default:
                return false;
        }

        if (nameLength <= 0 || nameOffset < 0 || nameOffset + nameLength > recordLength)
        {
            return false;
        }

        string? name = Marshal.PtrToStringUni(IntPtr.Add(pointer, nameOffset), nameLength / sizeof(char));
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        change = new UsnChangeRecord(frn, parentFrn, name, (attributes & FileAttributes.Directory) != 0, reason);
        return true;
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

        _disposed = true;
    }

    private static string NormalizeDrive(string driveName)
    {
        string trimmed = driveName.Trim().TrimEnd('\\', '/');
        if (trimmed.Length != 2 || trimmed[1] != ':')
        {
            throw new ArgumentException("驱动器必须类似 C: 或 C:\\。", nameof(driveName));
        }

        return trimmed.ToUpperInvariant();
    }
}

internal sealed record UsnJournalState(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);
internal sealed record UsnJournalReadResult(bool RequiresFullScan, UsnJournalState Journal, int RecordCount)
{
    internal static UsnJournalReadResult JournalRangeUnavailable(UsnJournalState state) => new(true, state, 0);
}

internal sealed record UsnChangeRecord(long Frn, long ParentFrn, string FileName, bool IsDirectory, uint Reason);
