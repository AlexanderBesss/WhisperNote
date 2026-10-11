using System.Windows;
using System.Windows.Media;

namespace WhisperNote.ViewModels;

// Shared visual-tree walk used by the custom title-bar drag handlers: both
// windows need to know whether a click originated inside a given control
// type before starting a drag.
internal static class VisualTree
{
    public static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
                return match;

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    // True when `child` sits inside `ancestor`. A click whose source is not
    // inside the window came from a popup's separate visual tree (e.g. a
    // combo box dropdown) and must not drive the window.
    public static bool IsWithin(DependencyObject? child, DependencyObject ancestor)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, ancestor))
                return true;

            child = VisualTreeHelper.GetParent(child);
        }

        return false;
    }
}
