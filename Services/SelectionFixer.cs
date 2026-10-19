namespace WhisperNote.Services;

public static class SelectionFixer
{
    internal static bool IsBlocked(bool alreadyRunning, bool recording, bool processing) =>
        alreadyRunning || recording || processing;

    // Ctrl+C must leave text on the clipboard that differs from the snapshot taken before it.
    internal static bool ClipboardChanged(string? before, string? after) =>
        !string.IsNullOrEmpty(after) && (before == null || after != before);

    // Only restore the snapshot while the clipboard still holds the text this flow last wrote.
    internal static bool ShouldRestore(string current, string lastWritten) => current == lastWritten;
}
