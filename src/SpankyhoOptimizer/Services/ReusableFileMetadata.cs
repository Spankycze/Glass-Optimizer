using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpankyhoOptimizer.Services;

// Atomic File.Replace may retain ALL three of destination size, mtime and
// creation timestamp. A fresh file ID distinguishes the changed KEEP data.
internal readonly record struct FileMetadataStamp(
    bool Exists, DateTime LastWriteUtc, long Length, DateTime CreationUtc,
    ulong FileIdentity = 0);

// File identity probing is opt-in: other hot-path metadata caches keep their
// original cheap FileInfo.Refresh. Only the saved user-consent profile needs
// the stronger guarantee when two processes replace a same-size JSON.
internal sealed class ReusableFileMetadata(string path, bool trackFileIdentity = false)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle, out ByHandleFileInformation info);

    private readonly object _gate = new();
    private readonly FileInfo _info = new(path);

    private static ulong GetPhysicalFileIdentity(string filename)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(filename,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(handle, out var info))
                return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            // An inaccessible profile must be reloaded fail-closed by its
            // caller; no exception from a best-effort metadata probe.
        }
        return 0;
    }

    internal FileMetadataStamp Read()
    {
        lock (_gate)
        {
            _info.Refresh();
            return _info.Exists
                ? new FileMetadataStamp(true, _info.LastWriteTimeUtc,
                    _info.Length, _info.CreationTimeUtc,
                    trackFileIdentity ? GetPhysicalFileIdentity(_info.FullName) : 0)
                : default;
        }
    }
}
