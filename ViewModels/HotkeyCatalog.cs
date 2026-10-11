using System.Collections.Generic;

namespace WhisperNote.ViewModels;

// Single source of truth for the hotkey choices: the display name of every
// virtual-key code and the list offered in Settings. The main window labels
// the live hook and the settings combo box build its rows from the same
// table, so the two can never drift apart.
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

    // The configured key always gets a row, even when it matches no well-known
    // code, so the combo box keeps the user's choice instead of resetting.
    public static List<(int Vk, string Name)> Options(int configuredVk)
    {
        var options = new List<(int Vk, string Name)>(WellKnown);
        if (!options.Exists(option => option.Vk == configuredVk))
            options.Add((configuredVk, NameOf(configuredVk)));
        return options;
    }
}
