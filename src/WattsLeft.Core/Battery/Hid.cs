using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WattsLeft.Battery;

/// <summary>Raw Win32 HID access, for controllers whose battery Windows doesn't surface.</summary>
internal static partial class Hid
{
    public const uint GenericRead = DeviceFile.GenericRead;
    public const uint GenericWrite = DeviceFile.GenericWrite;
    private const int HidpStatusSuccess = 0x00110000;

    /// <summary>Opens a HID device interface for overlapped I/O, shared so games can keep using it.</summary>
    public static SafeFileHandle Open(string path, uint access) => DeviceFile.Open(path, access, overlapped: true);

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_FreePreparsedData(IntPtr preparsed);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    /// <summary>The product name the collection gives now, opened with no access; null if it gives none.</summary>
    public static string? ProductName(string path)
    {
        using var handle = Open(path, 0);
        if (handle.IsInvalid)
            return null;
        var buffer = new byte[256];
        return HidD_GetProductString(handle, buffer, buffer.Length)
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    public static HidpCaps? GetCaps(SafeFileHandle device)
    {
        if (!HidD_GetPreparsedData(device, out var preparsed))
            return null;
        try
        {
            return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }
}
