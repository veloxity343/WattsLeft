using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WattsLeft.Battery;

/// <summary>
/// Opening a device interface (a HID collection, a Bluetooth service) by its path. Always shared for reading and
/// writing, so games and the maker's own software keep using the device alongside.
/// </summary>
internal static partial class DeviceFile
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FlagOverlapped = 0x40000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    /// <param name="access">0 to read only its details, which costs the device nothing.</param>
    /// <param name="overlapped">For asynchronous reads and writes, as a <see cref="FileStream"/> over it needs.</param>
    public static SafeFileHandle Open(string path, uint access, bool overlapped) =>
        CreateFile(path, access, ShareReadWrite, IntPtr.Zero, OpenExisting, overlapped ? FlagOverlapped : 0, IntPtr.Zero);
}
