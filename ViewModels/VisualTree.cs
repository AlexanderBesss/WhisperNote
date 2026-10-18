using System.Windows;
using System.Windows.Media;

namespace WhisperNote.ViewModels;

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
