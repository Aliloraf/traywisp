using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TrayWisp.Runtime.Trust;

// The few Win32 file APIs that .NET does not expose and the trust checks need: opening a file or
// directory without following reparse points or recalling remote data, querying attributes, link
// count and case sensitivity through the opened handle, the handle's final path, and a rename with
// write-through. Paths are passed with the \\?\ prefix; callers only pass paths that are already
// normalized (no "." or ".." segments, no trailing dots or spaces, backslash separators).
[SupportedOSPlatform("windows")]
internal static partial class WindowsFileApi
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;

    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;

    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x0010_0000;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;

    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;

    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;

    private const uint FileAttributeNormal = 0x80;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileFlagOpenNoRecall = 0x0010_0000;
    private const uint FileFlagSequentialScan = 0x0800_0000;

    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const int FileCaseSensitiveInfoClass = 23;
    private const uint CaseSensitiveDirectoryFlag = 0x1;

    private const uint FileTypeDisk = 1;

    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    // Longest path GetFinalPathNameByHandle can return, in characters, including the terminator.
    private const int MaxFinalPathLength = 32_768;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>How a handle is opened. Every mode refuses to follow reparse points and to recall remote data.</summary>
    public enum OpenMode
    {
        // Attributes only, shared with everyone, for checking ancestors. Takes no part in sharing checks.
        Inspect,

        // A directory that is listed and kept open: other programs cannot delete or rename it meanwhile.
        HoldDirectory,

        // A file read for hashing: other programs cannot write, delete or rename it meanwhile.
        ReadFileExclusive,

        // The trust store, read while the store lock is held.
        ReadStore,

        // The store lock file: created if missing, never shared.
        Lock,

        // An interpreter executable, read to check its format (directories open too, and are then rejected). Shared with
        // everyone, so it disturbs nothing.
        ReadShared,
    }

    public static string Extended(string normalizedPath) => ExtendedPrefix + normalizedPath;

    /// <summary>Opens a handle, or returns <see langword="null"/> and the Win32 error code.</summary>
    public static SafeFileHandle? Open(string normalizedPath, OpenMode mode, out int error)
    {
        const uint NoFollow = FileFlagOpenReparsePoint | FileFlagOpenNoRecall;
        (uint access, uint share, uint disposition, uint flags) = mode switch
        {
            OpenMode.Inspect => (FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, OpenExisting, NoFollow | FileFlagBackupSemantics),
            OpenMode.HoldDirectory => (FileReadData | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, OpenExisting, NoFollow | FileFlagBackupSemantics),
            OpenMode.ReadFileExclusive => (GenericRead, FileShareRead, OpenExisting, NoFollow | FileFlagSequentialScan),
            OpenMode.ReadStore => (GenericRead, FileShareRead, OpenExisting, NoFollow | FileFlagSequentialScan),
            OpenMode.Lock => (GenericRead | GenericWrite, 0u, OpenAlways, NoFollow | FileAttributeNormal),
            OpenMode.ReadShared => (GenericRead, FileShareRead | FileShareWrite | FileShareDelete, OpenExisting, NoFollow | FileFlagBackupSemantics),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        SafeFileHandle handle = CreateFile(Extended(normalizedPath), access, share, 0, disposition, flags, 0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        error = 0;
        return handle;
    }

    /// <summary>Reparse tag of an app execution alias, such as the Microsoft Store shortcuts in <c>WindowsApps</c>.</summary>
    public const uint ReparseTagAppExecutionLink = 0x8000_001B;

    /// <summary>Attributes of the opened entry itself (the reparse point, not its target, given the open flags).</summary>
    public static bool TryGetAttributes(SafeFileHandle handle, out FileAttributes attributes, out int error) =>
        TryGetAttributes(handle, out attributes, out _, out error);

    /// <summary>Attributes and reparse tag (zero unless it is a reparse point) of the opened entry itself.</summary>
    public static bool TryGetAttributes(SafeFileHandle handle, out FileAttributes attributes, out uint reparseTag, out int error)
    {
        if (GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out FileAttributeTagInformation info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            attributes = (FileAttributes)info.FileAttributes;
            reparseTag = (attributes & FileAttributes.ReparsePoint) != 0 ? info.ReparseTag : 0;
            error = 0;
            return true;
        }

        attributes = 0;
        reparseTag = 0;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>Whether the file is marked for deletion, and its hard link count.</summary>
    public static bool TryGetStandardInfo(SafeFileHandle handle, out bool deletePending, out uint linkCount, out int error)
    {
        if (GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInformation info, (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            deletePending = info.DeletePending != 0;
            linkCount = info.NumberOfLinks;
            error = 0;
            return true;
        }

        deletePending = false;
        linkCount = 0;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>
    /// Whether a directory has the per-directory case sensitivity flag. File systems and Windows versions
    /// without the flag report "not supported", which is treated as not case-sensitive: such file systems
    /// cannot hold case-variant names. Any other failure is returned as an error.
    /// </summary>
    public static bool TryIsCaseSensitiveDirectory(SafeFileHandle handle, out bool caseSensitive, out int error)
    {
        if (GetFileInformationByHandleEx(handle, FileCaseSensitiveInfoClass, out FileCaseSensitiveInformation info, (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
        {
            caseSensitive = (info.Flags & CaseSensitiveDirectoryFlag) != 0;
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        caseSensitive = false;
        if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction)
        {
            error = 0;
            return true;
        }

        return false;
    }

    public static bool IsDiskFile(SafeFileHandle handle) => GetFileType(handle) == FileTypeDisk;

    /// <summary>
    /// The identity of the opened file or directory: its volume serial number and 128-bit file identifier
    /// (FILE_ID_INFO). Two handles with the same identity refer to the same file system object. Used by the scheduler
    /// to notice that a plugin root was replaced by another directory with the same path.
    /// </summary>
    public static bool TryGetFileId(SafeFileHandle handle, out (ulong Volume, ulong Low, ulong High) id, out int error)
    {
        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInformation info, (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            id = (info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
            error = 0;
            return true;
        }

        id = default;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>
    /// The normalized final path of an open handle with its drive letter, without the <c>\\?\</c> prefix.
    /// A network path is returned as <c>\\server\share\...</c>.
    /// </summary>
    public static bool TryGetFinalPath(SafeFileHandle handle, out string path, out int error)
    {
        path = string.Empty;
        char[] buffer = new char[512];
        while (true)
        {
            uint length;
            unsafe
            {
                fixed (char* pointer = buffer)
                {
                    length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
                }
            }

            if (length == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            if (length < buffer.Length)
            {
                path = new string(buffer, 0, (int)length);
                break;
            }

            // The return value is the required size including the terminator.
            if (length > MaxFinalPathLength)
            {
                error = ErrorInsufficientBuffer;
                return false;
            }

            buffer = new char[length];
        }

        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
        {
            path = @"\\" + path[ExtendedUncPrefix.Length..];
        }
        else if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            path = path[ExtendedPrefix.Length..];
        }

        error = 0;
        return true;
    }

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/> in the same directory, replacing
    /// an existing destination only when <paramref name="replaceExisting"/> is set, and waiting until the
    /// rename has been written through.
    /// </summary>
    public static bool TryMove(string source, string destination, bool replaceExisting, out int error)
    {
        uint flags = MoveFileWriteThrough | (replaceExisting ? MoveFileReplaceExisting : 0);
        if (MoveFileEx(Extended(source), Extended(destination), flags))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    public static string Describe(int error) => Marshal.GetPInvokeErrorMessage(error);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInformation information, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileStandardInformation information, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileCaseSensitiveInformation information, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInformation information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFileType(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        public uint Flags;
    }

    // FILE_ID_INFO: a 64-bit volume serial number and a 128-bit FILE_ID_128.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }
}
