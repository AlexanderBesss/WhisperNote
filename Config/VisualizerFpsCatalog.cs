using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WhisperNote.Config;

public sealed class VisualizerFpsOption
{
    public int Fps { get; }
    public string Name { get; }

    public VisualizerFpsOption(int fps, string name)
    {
        Fps = fps;
        Name = name;
    }

    public override string ToString() => Name;
}

// The ladder is capped at the display refresh (frames cannot be shown faster
// than the panel draws them); the maximum over active devices is taken because
// the overlay may live on the fastest screen.
public static class VisualizerFpsCatalog
{
    public const int NominalFps = 30;
    public const int DefaultFps = 30;

    static readonly int[] Ladder = { 30, 60, 120, 144, 240, 360 };

    public static IReadOnlyList<VisualizerFpsOption> OptionsForMonitor(int savedFps)
    {
        var max = MaxRefreshHz();
        var options = Ladder
            .Where(fps => fps <= max)
            .Select(fps => new VisualizerFpsOption(fps, $"{fps} FPS"))
            .ToList();

        // A saved value from a faster screen stays listed so the combo never
        // opens blank; the pacer renders it at the current display's pace.
        if (savedFps > 0 && !options.Any(option => option.Fps == savedFps))
            options.Add(new VisualizerFpsOption(savedFps, $"{savedFps} FPS"));

        return options;
    }

    public static int Resolve(int fps) =>
        Ladder.Contains(fps) ? fps : DefaultFps;

    public static int MaxRefreshHz()
    {
        int max = 0;
        // Some sessions expose phantom devices with empty names; only active,
        // named devices carry usable current-mode data.
        for (uint i = 0; i < 16; i++)
        {
            var device = new DisplayDevice();
            if (!EnumDisplayDevices(null, i, ref device, 0))
                break;

            if (string.IsNullOrEmpty(device.DeviceName) ||
                (device.StateFlags & DisplayDeviceActive) == 0)
                continue;

            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettings(device.DeviceName, EnumCurrentSettings, ref mode) &&
                mode.dmDisplayFrequency > max)
                max = mode.dmDisplayFrequency;
        }

        if (max == 0)
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
                max = mode.dmDisplayFrequency;
        }

        // Headless or exotic sessions can fail enumeration; 60 is the safe floor.
        return max > 0 ? max : 60;
    }

    const int DisplayDeviceActive = 0x1;
    const int EnumCurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
