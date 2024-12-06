using System;
using System.Drawing;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;

namespace HealthBars;

public static class Extensions
{
    public static Color FromHex(uint hex)
    {
        return ConvertHelper.FromAbgr((hex << 8) + 0xff);
    }

    public static string FormatHp(this long hp)
    {
        if (Math.Abs(hp) >= 100_000_000)
        {
            return ((double)hp / 1_000_000).ToString("0M");
        }

        if (Math.Abs(hp) >= 10_000_000)
        {
            return ((double)hp / 1_000_000).ToString("0.0M");
        }

        if (Math.Abs(hp) >= 1_000_000)
        {
            return ((double)hp / 1_000_000).ToString("0.00M");
        }

        if (Math.Abs(hp) >= 100_000)
        {
            return ((double)hp / 1_000).ToString("0K");
        }

        if (Math.Abs(hp) >= 10_000)
        {
            return ((double)hp / 1_000).ToString("0.0K");
        }

        if (Math.Abs(hp) >= 1_000)
        {
            return ((double)hp / 1_000).ToString("0.00K");
        }

        return hp.ToString("0");
    }

    public static string FormatHp(this int hp)
    {
        return ((long)hp).FormatHp();
    }

    public static string FormatHp(this double hp)
    {
        return ((long)hp).FormatHp();
    }

    public static Color MultiplyAlpha(this Color color, float alphaMultiplier)
    {
        return Color.FromArgb((byte)(color.A * alphaMultiplier), color);
    }

    public static Color MultiplyAlpha(this ColorNode color, float alphaMultiplier)
    {
        return Color.FromArgb((byte)(color.Value.A * alphaMultiplier), color);
    }

    public static string Truncate(this string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public static string StageNameSafe(this AnimationStage stage)
    {
        try
        {
            return stage.StageName;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
            return "";
        }
    }
}