using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeerSharp.PieceWriter;

internal static class SparseFileHelper
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    public static bool TrySetSparse(SafeFileHandle handle, out int error)
    {
        error = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (handle.IsInvalid)
        {
            return false;
        }

        bool result = DeviceIoControl(
            handle,
            FSCTL_SET_SPARSE,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

        if (!result)
        {
            error = Marshal.GetLastWin32Error();
        }

        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
