using System.Windows;
using System.Windows.Media;

namespace SC3RGBController.UI.Themes;

/// <summary>
/// Swaps the app's design tokens (see Colors.xaml) for Windows system colors when
/// Windows High Contrast is on, so every surface follows the user's contrast theme.
/// Values are written directly into the given (application-level) dictionary, which
/// takes precedence over the merged Colors.xaml dictionary.
/// </summary>
public static class ThemeManager
{
    /// <summary>True while a Windows High Contrast theme is active.</summary>
    public static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// False in High Contrast: callers that recolor AccentBrush / AccentForegroundBrush /
    /// AccentTextBrush from the user's chosen LED color should skip doing so, keeping the
    /// system Highlight colors.
    /// </summary>
    public static bool AllowDynamicAccent => !SystemParameters.HighContrast;

    /// <summary>
    /// If High Contrast is enabled, replaces the app color/brush resource keys with
    /// system-color equivalents. Call before any window is created: most markup uses
    /// StaticResource, which is resolved once when an element loads.
    /// </summary>
    public static void ApplyHighContrastIfEnabled(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!SystemParameters.HighContrast)
        {
            return;
        }

        // Colors
        Set(resources, SystemColors.WindowColor, "WindowColor", "PanelColor", "PanelAltColor", "InputColor");
        Set(resources, SystemColors.ControlColor, "HoverColor", "PressedColor");
        Set(resources, SystemColors.WindowTextColor,
            "TextColor", "SecondaryTextColor", "MutedColor", "BorderColor", "ControlStrokeColor");
        Set(resources, SystemColors.HighlightColor, "AccentColor");

        // Surfaces
        Set(resources, SystemColors.WindowBrush,
            "WindowBrush", "PanelBrush", "PanelAltBrush", "InputBrush",
            "InfoBarBackgroundBrush", "WarningBarBackgroundBrush", "SuccessBarBackgroundBrush");
        Set(resources, SystemColors.ControlBrush, "HoverBrush", "PressedBrush");

        // Text, strokes and status indicators
        Set(resources, SystemColors.WindowTextBrush,
            "TextBrush", "SecondaryTextBrush", "MutedBrush",
            "BorderBrush", "ControlStrokeBrush",
            "ConnectedBrush", "DisconnectedBrush", "WarningBrush");

        // Focus and accent
        Set(resources, SystemColors.HighlightBrush, "FocusRingBrush", "AccentBrush");
        Set(resources, SystemColors.HighlightTextBrush, "AccentForegroundBrush");
        Set(resources, SystemColors.HotTrackBrush, "AccentTextBrush");
    }

    private static void Set(ResourceDictionary resources, object value, params string[] keys)
    {
        foreach (string key in keys)
        {
            resources[key] = value;
        }
    }
}
