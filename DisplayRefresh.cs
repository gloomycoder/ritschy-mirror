using System.Runtime.InteropServices;

namespace RitschyMirror;

/// <summary>Current refresh rate of a display (GDI device name, e.g. <c>\\.\DISPLAY4</c>) — for logs.</summary>
internal static class DisplayRefresh
{
    private const int EnumCurrentSettings = -1;

    /// <returns>Refresh rate in Hz; 0 if unknown (also for the "hardware default" values 0 and 1).</returns>
    public static int Hz(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref dm)) return 0;
        return dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 0;
    }

    /// <summary>"60 Hz" or "? Hz" for logs.</summary>
    public static string Text(int hz) => hz > 0 ? $"{hz} Hz" : "? Hz";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
