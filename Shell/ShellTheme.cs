using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SelfContainedDeployment
{
    internal static class ShellTheme
    {
        public static ElementTheme ResolveEffectiveTheme(ElementTheme requestedTheme, ElementTheme actualTheme = ElementTheme.Default)
        {
            if (requestedTheme != ElementTheme.Default)
            {
                return requestedTheme;
            }

            return actualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        }

        public static Windows.UI.Color ResolveColorForTheme(ElementTheme theme, string key, Windows.UI.Color fallbackColor)
        {
            Windows.UI.Color color = (theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark, key) switch
            {
                (ElementTheme.Light, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF5, 0xF8),
                (ElementTheme.Light, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF9, 0xFB, 0xFD),
                (ElementTheme.Light, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFA),
                (ElementTheme.Light, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0xDF, 0xE8, 0xF1),
                (ElementTheme.Light, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xB8, 0xC6, 0xD4),
                (ElementTheme.Light, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55),
                (ElementTheme.Light, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1E, 0x23),
                (ElementTheme.Light, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x39, 0x42, 0x4D),
                (ElementTheme.Light, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x55, 0x60, 0x6D),
                (ElementTheme.Light, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
                (ElementTheme.Light, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04),
                (ElementTheme.Light, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xDC, 0x26, 0x26),
                (ElementTheme.Light, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Dark, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10),
                (ElementTheme.Dark, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x10, 0x12, 0x16),
                (ElementTheme.Dark, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B),
                (ElementTheme.Dark, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0x17, 0x1A, 0x20),
                (ElementTheme.Dark, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x21, 0x27),
                (ElementTheme.Dark, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xC9, 0xCD, 0xD4),
                (ElementTheme.Dark, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6),
                (ElementTheme.Dark, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xA7, 0xAD, 0xB7),
                (ElementTheme.Dark, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x80, 0x8B),
                (ElementTheme.Dark, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80),
                (ElementTheme.Dark, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                (ElementTheme.Dark, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
                (ElementTheme.Dark, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                _ => default,
            };

            return color != default || !TryResolveResourceColor(key, out Windows.UI.Color resourceColor)
                ? (color != default ? color : fallbackColor)
                : resourceColor;
        }

        public static Brush ResolveBrushForTheme(ElementTheme theme, string key, Windows.UI.Color fallbackColor = default)
        {
            return new SolidColorBrush(ResolveColorForTheme(theme, key, fallbackColor));
        }

        private static bool TryResolveResourceColor(string key, out Windows.UI.Color color)
        {
            if (Application.Current?.Resources.TryGetValue(key, out object resource) == true)
            {
                switch (resource)
                {
                    case SolidColorBrush solid:
                        color = solid.Color;
                        return true;
                    case Windows.UI.Color directColor:
                        color = directColor;
                        return true;
                }
            }

            color = default;
            return false;
        }
    }
}
