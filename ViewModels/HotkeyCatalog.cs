using System.Collections.Generic;

namespace WhisperNote.ViewModels;

internal static class HotkeyCatalog
{
    static readonly (int Vk, string Name)[] WellKnown =
    {
        (0xA3, "Right Ctrl"),
        (0xA2, "Left Ctrl"),
        (0xA5, "Right Alt"),
        (0xA4, "Left Alt"),
        (0x14, "Caps Lock"),
        (0xA0, "Left Shift"),
        (0xA1, "Right Shift"),
        (0x10, "Shift"),
        (0x11, "Ctrl"),
        (0x12, "Alt"),
        (0x5B, "Left Win"),
        (0x5C, "Right Win"),
    };

    public static string NameOf(int vk)
    {
        foreach (var (code, name) in WellKnown)
        {
            if (code == vk)
                return name;
        }
        return $"VK_{vk:X}";
    }

    public static List<(int Vk, string Name)> Options(int configuredVk)
    {
        var options = new List<(int Vk, string Name)>(WellKnown);
        if (!options.Exists(option => option.Vk == configuredVk))
            options.Add((configuredVk, NameOf(configuredVk)));
        return options;
    }
}
