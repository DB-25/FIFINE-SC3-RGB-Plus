using System.Windows.Media;
using SC3RGBController.Models;

namespace SC3RGBController.Services;

/// <summary>
/// The hardware lighting state, independent of any WPF control.  Both the
/// startup host and the normal window use this policy so a hidden startup
/// session produces the same output as the Lighting page.
/// </summary>
public readonly record struct SavedLightingState(
    Color BaseColor,
    int Brightness,
    bool IsEnabled,
    LightingEffect Effect,
    int EffectSpeed)
{
    public static SavedLightingState From(AppSettings settings)
    {
        Color color = TryParseColor(settings.LastHex, out Color parsed)
            ? parsed
            : Color.FromRgb(
                (byte)Math.Clamp(settings.Red, 0, 255),
                (byte)Math.Clamp(settings.Green, 0, 255),
                (byte)Math.Clamp(settings.Blue, 0, 255));
        LightingEffect effect = Enum.TryParse(settings.Effect, true, out LightingEffect parsedEffect)
            ? parsedEffect : LightingEffect.Static;
        int speed = effect switch
        {
            LightingEffect.Breathing => settings.BreathingSpeed,
            LightingEffect.Rainbow => settings.RainbowSpeed,
            LightingEffect.Pulse => settings.PulseSpeed,
            LightingEffect.ColorCycle => settings.ColorCycleSpeed,
            _ => EffectSpeedPolicy.DefaultSpeed
        };
        return new(color, Math.Clamp(settings.Brightness, 0, 100), settings.IsLightingEnabled,
            effect, EffectSpeedPolicy.Normalize(speed));
    }

    public Color GetOutput(DateTime startedAt, DateTime now)
    {
        if (!IsEnabled) return Colors.Black;

        double cycle = (now - startedAt).TotalSeconds * EffectSpeedPolicy.CyclesPerSecond(Effect, EffectSpeed);
        Color effectColor = BaseColor;
        double level = 1;
        switch (Effect)
        {
            case LightingEffect.Breathing:
                level = 0.16 + 0.84 * (0.5 + 0.5 * Math.Sin(cycle * Math.PI * 2));
                break;
            case LightingEffect.Rainbow:
                effectColor = ColorFromHsv(cycle * 360, 1, 1);
                break;
            case LightingEffect.Pulse:
                level = 0.12 + 0.88 * Math.Pow(0.5 + 0.5 * Math.Sin(cycle * Math.PI * 2), 4);
                break;
            case LightingEffect.ColorCycle:
                (double hue, double saturation, double value) = RgbToHsv(BaseColor);
                Color[] palette =
                [
                    BaseColor,
                    ColorFromHsv(hue + 120, Math.Max(saturation, 0.85), Math.Max(value, 0.85)),
                    ColorFromHsv(hue + 240, Math.Max(saturation, 0.85), Math.Max(value, 0.85))
                ];
                double position = cycle % palette.Length;
                if (position < 0) position += palette.Length;
                int index = (int)Math.Floor(position);
                double transition = position - index;
                transition = transition * transition * (3 - 2 * transition);
                effectColor = Blend(palette[index], palette[(index + 1) % palette.Length], transition);
                break;
        }

        return Scale(effectColor, Brightness / 100.0 * level);
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) ||
            !byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) ||
            !byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue)) return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static Color Blend(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static Color Scale(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp((int)Math.Round(color.R * amount), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.G * amount), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.B * amount), 0, 255));

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        double m = value - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
