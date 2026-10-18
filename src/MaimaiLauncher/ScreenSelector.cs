using System;
using System.Linq;
using System.Windows.Forms;

namespace MaimaiLauncher;

public static class ScreenSelector
{
    public static Screen Select(ScreenConfig config)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException("没有检测到显示器。");

        if (!string.IsNullOrWhiteSpace(config.DeviceName))
        {
            var named = screens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, config.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (named != null)
                return named;
        }

        if (string.Equals(config.Target, "primary", StringComparison.OrdinalIgnoreCase))
            return Screen.PrimaryScreen ?? screens[0];

        if (string.Equals(config.Target, "landscape", StringComparison.OrdinalIgnoreCase))
        {
            var landscape = screens
                .Where(s => s.Bounds.Width > s.Bounds.Height)
                .OrderByDescending(s => s.Bounds.Width)
                .FirstOrDefault();
            return landscape ?? Screen.PrimaryScreen ?? screens[0];
        }

        var portrait = screens
            .Where(s => s.Bounds.Height > s.Bounds.Width)
            .OrderByDescending(s => s.Bounds.Height)
            .FirstOrDefault();

        return portrait ?? Screen.PrimaryScreen ?? screens[0];
    }
}
