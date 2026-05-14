using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeerSharp.PieceWriter;

/// <summary>
/// Handles OS-specific disk allocation optimizations to bypass synchronous zero-filling.
/// </summary>
internal static class DiskAllocation
{
    // Skip unit testing for OS-specific interop

    /// <summary>
    /// Attempts to pre-allocate file space without zero-filling to prevent fragmentation
    /// and I/O lockups during torrent startup. Falls back to standard allocation/sparse files on failure.
    /// </summary>
    public static void Allocate(FileStream fs, long size, Action<FileStream> enableSparseFallback)
    {
        if (size <= 0 || fs.Length >= size)
        {
            return;
        }

        bool preallocated = false;

        if (OperatingSystem.IsLinux())
        {
            preallocated = TryPosixFallocate(fs.SafeFileHandle, size);
        }

        if (!preallocated)
        {
            // Fallback to Sparse Files before SetLength
            enableSparseFallback(fs);

            if (fs.Length < size)
            {
                fs.SetLength(size);
            }

            // On Windows, try SetFileValidData after extending the file to bypass zero-filling
            // and reserve contiguous space if the user has SE_MANAGE_VOLUME_NAME privilege.
            if (OperatingSystem.IsWindows())
            {
                TrySetFileValidData(fs.SafeFileHandle, size);
            }
        }
    }

    private static void TrySetFileValidData(SafeFileHandle handle, long size)
    {
        try
        {
            SetFileValidData(handle, size);
        }
        catch
        {
            // Ignore
        }
    }

    private static bool TryPosixFallocate(SafeFileHandle handle, long size)
    {
        try
        {
            // posix_fallocate ensures contiguous space is allocated without zero-filling.
            int result = posix_fallocate(handle, 0, size);
            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileValidData(SafeFileHandle hFile, long ValidDataLength);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fallocate(SafeFileHandle fd, long offset, long len);
}
