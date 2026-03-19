using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace SelfContainedDeployment
{
    internal static class ShellThemePackIds
    {
        public const string Graphite = "graphite";
        public const string Harbor = "harbor";
        public const string Moss = "moss";
        public const string Copper = "copper";
    }

    internal static class ShellTheme
    {
        public const string DefaultPackId = ShellThemePackIds.Graphite;

        private static readonly IReadOnlyDictionary<string, ShellThemePack> Packs = CreatePacks();

        public static ElementTheme ResolveEffectiveTheme(ElementTheme requestedTheme, ElementTheme actualTheme = ElementTheme.Default)
        {
            if (requestedTheme != ElementTheme.Default)
            {
                return requestedTheme;
            }

            return actualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        }

        public static string NormalizePackId(string packId)
        {
            return ResolvePack(packId).Id;
        }

        public static bool IsKnownPack(string packId)
        {
            return !string.IsNullOrWhiteSpace(packId) && Packs.ContainsKey(packId.Trim());
        }

        public static void ApplyThemePackResources(string packId)
        {
            if (Application.Current?.Resources is not ResourceDictionary appResources)
            {
                return;
            }

            ShellThemePack pack = ResolvePack(packId);
            ApplyPaletteToThemeDictionary(ResolveThemeDictionary(appResources, "Default"), pack.DarkPalette);
            ApplyPaletteToThemeDictionary(ResolveThemeDictionary(appResources, "Light"), pack.LightPalette);
        }

        public static Windows.UI.Color ResolveColorForTheme(ElementTheme theme, string key, Windows.UI.Color fallbackColor)
        {
            ShellThemePack pack = ResolvePack(SampleConfig.CurrentThemePackId);
            IReadOnlyDictionary<string, Windows.UI.Color> palette = pack.ResolvePalette(theme);
            if (palette.TryGetValue(key, out Windows.UI.Color color))
            {
                return color;
            }

            return TryResolveResourceColor(key, out Windows.UI.Color resourceColor)
                ? resourceColor
                : fallbackColor;
        }

        public static Brush ResolveBrushForTheme(ElementTheme theme, string key, Windows.UI.Color fallbackColor = default)
        {
            return new SolidColorBrush(ResolveColorForTheme(theme, key, fallbackColor));
        }

        private static IReadOnlyDictionary<string, ShellThemePack> CreatePacks()
        {
            return new Dictionary<string, ShellThemePack>(StringComparer.OrdinalIgnoreCase)
            {
                [ShellThemePackIds.Graphite] = new(
                    ShellThemePackIds.Graphite,
                    CreatePalette(
                        pageBackground: Color(0xF2, 0xF5, 0xF8),
                        paneBackground: Color(0xF2, 0xF5, 0xF8),
                        surfaceBackground: Color(0xF9, 0xFB, 0xFD),
                        mutedSurface: Color(0xF2, 0xF6, 0xFA),
                        brandMarkBackground: Color(0xF9, 0xFB, 0xFD),
                        brandMarkBorder: Color(0xCC, 0xD6, 0xE0),
                        border: Color(0xB8, 0xC6, 0xD4),
                        paneDivider: Color(0xC6, 0xD3, 0xDF),
                        paneActiveBorder: Color(0x33, 0x41, 0x55),
                        navHover: Color(0xE7, 0xEE, 0xF5),
                        navActive: Color(0xDF, 0xE8, 0xF1),
                        textPrimary: Color(0x1A, 0x1E, 0x23),
                        textSecondary: Color(0x39, 0x42, 0x4D),
                        textTertiary: Color(0x55, 0x60, 0x6D),
                        success: Color(0x16, 0xA3, 0x4A),
                        warning: Color(0xCA, 0x8A, 0x04),
                        danger: Color(0xDC, 0x26, 0x26),
                        info: Color(0x25, 0x63, 0xEB),
                        terminal: Color(0x0F, 0x76, 0x6E),
                        csharp: Color(0x15, 0x80, 0x3D),
                        typeScript: Color(0x0F, 0x6C, 0xAD),
                        javaScript: Color(0xA1, 0x62, 0x07),
                        script: Color(0x1D, 0x4E, 0xD8),
                        config: Color(0x47, 0x55, 0x69),
                        markdown: Color(0x6B, 0x72, 0x80),
                        markup: Color(0xC2, 0x41, 0x0C),
                        style: Color(0x0F, 0x76, 0x6E),
                        browserChromeBackground: Color(0xEA, 0xF1, 0xF7),
                        browserChromeRaised: Color(0xF7, 0xFA, 0xFC),
                        browserChromeField: Color(0xFF, 0xFF, 0xFF),
                        browserChromeDivider: Color(0xC8, 0xD5, 0xE1),
                        browserChromeFieldBorder: Color(0xAF, 0xC1, 0xD1),
                        browserChromeTabBackground: Color(0xFF, 0xFF, 0xFF),
                        browserChromeTabBorder: Color(0x8F, 0xA6, 0xBC),
                        browserChromeTabForeground: Color(0x16, 0x20, 0x2B),
                        browserChromeTabInactiveForeground: Color(0x44, 0x53, 0x61),
                        browserChromeGlyph: Color(0x31, 0x41, 0x50),
                        tabBackground: Color(0xF9, 0xFB, 0xFD),
                        tabHeaderBackground: Color(0xEE, 0xF3, 0xF7),
                        tabHeaderSelected: Color(0xF9, 0xFB, 0xFD),
                        tabHeaderPointerOver: Color(0xE8, 0xEE, 0xF4),
                        tabHeaderPressed: Color(0xDD, 0xE6, 0xEF),
                        tabHeaderDisabled: Color(0xEE, 0xF3, 0xF7),
                        tabHeaderForeground: Color(0x61, 0x70, 0x7C),
                        tabHeaderForegroundSelected: Color(0x1A, 0x1E, 0x23),
                        tabHeaderForegroundDisabled: Color(0x9F, 0xAA, 0xB7),
                        tabButtonForeground: Color(0x0F, 0x76, 0x6E),
                        tabButtonForegroundPressed: Color(0x0D, 0x94, 0x88),
                        tabButtonForegroundDisabled: Color(0x9F, 0xAA, 0xB7),
                        tabScrollForeground: Color(0x52, 0x60, 0x6D),
                        tabScrollForegroundPressed: Color(0x1A, 0x1E, 0x23),
                        tabScrollForegroundDisabled: Color(0x9F, 0xAA, 0xB7),
                        chromeBackground: Color(0xF5, 0xF6, 0xF8),
                        chromeHoverBackground: Color(0xF0, 0xF3, 0xF6),
                        chromePressedBackground: Color(0xE7, 0xEB, 0xF0),
                        chromeForeground: Color(0x1B, 0x1F, 0x24),
                        chromeInactiveForeground: Color(0x7E, 0x86, 0x91)),
                    CreatePalette(
                        pageBackground: Color(0x0C, 0x0D, 0x10),
                        paneBackground: Color(0x0C, 0x0D, 0x10),
                        surfaceBackground: Color(0x10, 0x12, 0x16),
                        mutedSurface: Color(0x14, 0x16, 0x1B),
                        brandMarkBackground: Color(0x13, 0x16, 0x1B),
                        brandMarkBorder: Color(0x26, 0x2B, 0x33),
                        border: Color(0x1E, 0x21, 0x27),
                        paneDivider: Color(0x1A, 0x1D, 0x23),
                        paneActiveBorder: Color(0xC9, 0xCD, 0xD4),
                        navHover: Color(0x13, 0x16, 0x1B),
                        navActive: Color(0x17, 0x1A, 0x20),
                        textPrimary: Color(0xF3, 0xF4, 0xF6),
                        textSecondary: Color(0xA7, 0xAD, 0xB7),
                        textTertiary: Color(0x7A, 0x80, 0x8B),
                        success: Color(0x4A, 0xDE, 0x80),
                        warning: Color(0xFB, 0xBF, 0x24),
                        danger: Color(0xF8, 0x71, 0x71),
                        info: Color(0x60, 0xA5, 0xFA),
                        terminal: Color(0x2D, 0xD4, 0xBF),
                        csharp: Color(0x4A, 0xDE, 0x80),
                        typeScript: Color(0x38, 0xBD, 0xF8),
                        javaScript: Color(0xFA, 0xCC, 0x15),
                        script: Color(0x60, 0xA5, 0xFA),
                        config: Color(0x94, 0xA3, 0xB8),
                        markdown: Color(0xA1, 0xA1, 0xAA),
                        markup: Color(0xFB, 0x92, 0x3C),
                        style: Color(0x2D, 0xD4, 0xBF),
                        browserChromeBackground: Color(0x10, 0x13, 0x18),
                        browserChromeRaised: Color(0x17, 0x1C, 0x23),
                        browserChromeField: Color(0x0D, 0x11, 0x16),
                        browserChromeDivider: Color(0x23, 0x2A, 0x34),
                        browserChromeFieldBorder: Color(0x2D, 0x37, 0x44),
                        browserChromeTabBackground: Color(0x17, 0x1E, 0x27),
                        browserChromeTabBorder: Color(0x34, 0x41, 0x52),
                        browserChromeTabForeground: Color(0xF3, 0xF4, 0xF6),
                        browserChromeTabInactiveForeground: Color(0xAE, 0xB5, 0xC0),
                        browserChromeGlyph: Color(0xCC, 0xD3, 0xDD),
                        tabBackground: Color(0x10, 0x12, 0x16),
                        tabHeaderBackground: Color(0x10, 0x12, 0x16),
                        tabHeaderSelected: Color(0x15, 0x18, 0x1D),
                        tabHeaderPointerOver: Color(0x13, 0x16, 0x1B),
                        tabHeaderPressed: Color(0x17, 0x1A, 0x20),
                        tabHeaderDisabled: Color(0x10, 0x12, 0x16),
                        tabHeaderForeground: Color(0xA7, 0xAD, 0xB7),
                        tabHeaderForegroundSelected: Color(0xF3, 0xF4, 0xF6),
                        tabHeaderForegroundDisabled: Color(0x7A, 0x80, 0x8B),
                        tabButtonForeground: Color(0x2D, 0xD4, 0xBF),
                        tabButtonForegroundPressed: Color(0x5E, 0xEA, 0xD4),
                        tabButtonForegroundDisabled: Color(0x7A, 0x80, 0x8B),
                        tabScrollForeground: Color(0xA7, 0xAD, 0xB7),
                        tabScrollForegroundPressed: Color(0xF3, 0xF4, 0xF6),
                        tabScrollForegroundDisabled: Color(0x7A, 0x80, 0x8B),
                        chromeBackground: Color(0x0C, 0x0D, 0x10),
                        chromeHoverBackground: Color(0x13, 0x16, 0x1B),
                        chromePressedBackground: Color(0x17, 0x1A, 0x20),
                        chromeForeground: Color(0xF3, 0xF4, 0xF6),
                        chromeInactiveForeground: Color(0x7A, 0x80, 0x8B))),
                [ShellThemePackIds.Harbor] = new(
                    ShellThemePackIds.Harbor,
                    CreatePalette(
                        pageBackground: Color(0xF0, 0xF5, 0xF9),
                        paneBackground: Color(0xF0, 0xF5, 0xF9),
                        surfaceBackground: Color(0xF7, 0xFB, 0xFE),
                        mutedSurface: Color(0xEA, 0xF1, 0xF7),
                        brandMarkBackground: Color(0xF7, 0xFB, 0xFE),
                        brandMarkBorder: Color(0xC8, 0xD7, 0xE3),
                        border: Color(0xB3, 0xC4, 0xD2),
                        paneDivider: Color(0xC2, 0xD2, 0xDE),
                        paneActiveBorder: Color(0x2B, 0x4D, 0x66),
                        navHover: Color(0xE1, 0xEB, 0xF4),
                        navActive: Color(0xD7, 0xE3, 0xEE),
                        textPrimary: Color(0x17, 0x21, 0x2B),
                        textSecondary: Color(0x39, 0x53, 0x64),
                        textTertiary: Color(0x5B, 0x73, 0x84),
                        success: Color(0x0F, 0x8A, 0x63),
                        warning: Color(0xA1, 0x62, 0x07),
                        danger: Color(0xC2, 0x41, 0x3B),
                        info: Color(0x25, 0x63, 0xEB),
                        terminal: Color(0x0F, 0x76, 0x6E),
                        csharp: Color(0x15, 0x73, 0x47),
                        typeScript: Color(0x16, 0x5D, 0x91),
                        javaScript: Color(0x9A, 0x67, 0x00),
                        script: Color(0x1D, 0x4E, 0xD8),
                        config: Color(0x48, 0x64, 0x7A),
                        markdown: Color(0x6B, 0x7E, 0x8F),
                        markup: Color(0xC0, 0x56, 0x21),
                        style: Color(0x0F, 0x76, 0x6E),
                        browserChromeBackground: Color(0xE7, 0xF0, 0xF6),
                        browserChromeRaised: Color(0xF7, 0xFB, 0xFE),
                        browserChromeField: Color(0xFF, 0xFF, 0xFF),
                        browserChromeDivider: Color(0xBF, 0xD0, 0xDE),
                        browserChromeFieldBorder: Color(0xA5, 0xBA, 0xCC),
                        browserChromeTabBackground: Color(0xFF, 0xFF, 0xFF),
                        browserChromeTabBorder: Color(0x7E, 0x98, 0xAD),
                        browserChromeTabForeground: Color(0x17, 0x21, 0x2B),
                        browserChromeTabInactiveForeground: Color(0x54, 0x70, 0x87),
                        browserChromeGlyph: Color(0x35, 0x54, 0x6A),
                        tabBackground: Color(0xF7, 0xFB, 0xFE),
                        tabHeaderBackground: Color(0xEB, 0xF2, 0xF8),
                        tabHeaderSelected: Color(0xF7, 0xFB, 0xFE),
                        tabHeaderPointerOver: Color(0xE3, 0xEC, 0xF4),
                        tabHeaderPressed: Color(0xD6, 0xE3, 0xEE),
                        tabHeaderDisabled: Color(0xEB, 0xF2, 0xF8),
                        tabHeaderForeground: Color(0x5A, 0x6F, 0x80),
                        tabHeaderForegroundSelected: Color(0x17, 0x21, 0x2B),
                        tabHeaderForegroundDisabled: Color(0x9E, 0xAE, 0xBB),
                        tabButtonForeground: Color(0x1D, 0x6E, 0x96),
                        tabButtonForegroundPressed: Color(0x1F, 0x83, 0xB2),
                        tabButtonForegroundDisabled: Color(0x9E, 0xAE, 0xBB),
                        tabScrollForeground: Color(0x50, 0x67, 0x7B),
                        tabScrollForegroundPressed: Color(0x17, 0x21, 0x2B),
                        tabScrollForegroundDisabled: Color(0x9E, 0xAE, 0xBB),
                        chromeBackground: Color(0xF2, 0xF6, 0xF9),
                        chromeHoverBackground: Color(0xE9, 0xF0, 0xF5),
                        chromePressedBackground: Color(0xDD, 0xE7, 0xEF),
                        chromeForeground: Color(0x17, 0x21, 0x2B),
                        chromeInactiveForeground: Color(0x7A, 0x8D, 0x9C)),
                    CreatePalette(
                        pageBackground: Color(0x0B, 0x0F, 0x14),
                        paneBackground: Color(0x0B, 0x0F, 0x14),
                        surfaceBackground: Color(0x10, 0x18, 0x21),
                        mutedSurface: Color(0x15, 0x20, 0x2A),
                        brandMarkBackground: Color(0x12, 0x1C, 0x26),
                        brandMarkBorder: Color(0x22, 0x32, 0x40),
                        border: Color(0x22, 0x34, 0x44),
                        paneDivider: Color(0x18, 0x24, 0x30),
                        paneActiveBorder: Color(0xC7, 0xDC, 0xEA),
                        navHover: Color(0x12, 0x20, 0x2B),
                        navActive: Color(0x16, 0x26, 0x33),
                        textPrimary: Color(0xED, 0xF4, 0xFA),
                        textSecondary: Color(0xA3, 0xB8, 0xC8),
                        textTertiary: Color(0x76, 0x88, 0x98),
                        success: Color(0x3C, 0xCB, 0x90),
                        warning: Color(0xD3, 0xA4, 0x41),
                        danger: Color(0xF0, 0x8A, 0x81),
                        info: Color(0x8B, 0xBE, 0xFF),
                        terminal: Color(0x35, 0xD0, 0xBE),
                        csharp: Color(0x66, 0xD3, 0x93),
                        typeScript: Color(0x66, 0xB7, 0xE5),
                        javaScript: Color(0xE9, 0xC4, 0x6A),
                        script: Color(0x7F, 0xB5, 0xFF),
                        config: Color(0xB0, 0xC1, 0xD1),
                        markdown: Color(0x9E, 0xAC, 0xB8),
                        markup: Color(0xF0, 0xA3, 0x6A),
                        style: Color(0x4D, 0xD2, 0xC2),
                        browserChromeBackground: Color(0x0F, 0x17, 0x20),
                        browserChromeRaised: Color(0x15, 0x22, 0x2D),
                        browserChromeField: Color(0x10, 0x1B, 0x24),
                        browserChromeDivider: Color(0x29, 0x40, 0x50),
                        browserChromeFieldBorder: Color(0x36, 0x51, 0x65),
                        browserChromeTabBackground: Color(0x18, 0x26, 0x34),
                        browserChromeTabBorder: Color(0x5F, 0x84, 0xA0),
                        browserChromeTabForeground: Color(0xED, 0xF4, 0xFA),
                        browserChromeTabInactiveForeground: Color(0x8F, 0xA5, 0xB8),
                        browserChromeGlyph: Color(0xB9, 0xCE, 0xDD),
                        tabBackground: Color(0x10, 0x18, 0x21),
                        tabHeaderBackground: Color(0x10, 0x18, 0x21),
                        tabHeaderSelected: Color(0x15, 0x20, 0x2A),
                        tabHeaderPointerOver: Color(0x12, 0x20, 0x2B),
                        tabHeaderPressed: Color(0x16, 0x26, 0x33),
                        tabHeaderDisabled: Color(0x10, 0x18, 0x21),
                        tabHeaderForeground: Color(0xA3, 0xB8, 0xC8),
                        tabHeaderForegroundSelected: Color(0xED, 0xF4, 0xFA),
                        tabHeaderForegroundDisabled: Color(0x76, 0x88, 0x98),
                        tabButtonForeground: Color(0x52, 0xAE, 0xDB),
                        tabButtonForegroundPressed: Color(0x82, 0xCA, 0xFF),
                        tabButtonForegroundDisabled: Color(0x76, 0x88, 0x98),
                        tabScrollForeground: Color(0xA3, 0xB8, 0xC8),
                        tabScrollForegroundPressed: Color(0xED, 0xF4, 0xFA),
                        tabScrollForegroundDisabled: Color(0x76, 0x88, 0x98),
                        chromeBackground: Color(0x0B, 0x0F, 0x14),
                        chromeHoverBackground: Color(0x12, 0x20, 0x2B),
                        chromePressedBackground: Color(0x16, 0x26, 0x33),
                        chromeForeground: Color(0xED, 0xF4, 0xFA),
                        chromeInactiveForeground: Color(0x76, 0x88, 0x98))),
                [ShellThemePackIds.Moss] = new(
                    ShellThemePackIds.Moss,
                    CreatePalette(
                        pageBackground: Color(0xF3, 0xF6, 0xF1),
                        paneBackground: Color(0xF3, 0xF6, 0xF1),
                        surfaceBackground: Color(0xF8, 0xFB, 0xF6),
                        mutedSurface: Color(0xED, 0xF4, 0xEB),
                        brandMarkBackground: Color(0xF8, 0xFB, 0xF6),
                        brandMarkBorder: Color(0xC8, 0xD3, 0xC4),
                        border: Color(0xB8, 0xC6, 0xB4),
                        paneDivider: Color(0xC4, 0xD0, 0xBF),
                        paneActiveBorder: Color(0x32, 0x4C, 0x3A),
                        navHover: Color(0xE6, 0xEE, 0xE5),
                        navActive: Color(0xDC, 0xE7, 0xDB),
                        textPrimary: Color(0x1C, 0x24, 0x1D),
                        textSecondary: Color(0x44, 0x55, 0x45),
                        textTertiary: Color(0x67, 0x76, 0x68),
                        success: Color(0x15, 0x80, 0x3D),
                        warning: Color(0xA1, 0x62, 0x07),
                        danger: Color(0xB9, 0x2C, 0x2C),
                        info: Color(0x2F, 0x6E, 0xA3),
                        terminal: Color(0x0F, 0x76, 0x6E),
                        csharp: Color(0x16, 0x65, 0x34),
                        typeScript: Color(0x2D, 0x6A, 0x88),
                        javaScript: Color(0x93, 0x6D, 0x08),
                        script: Color(0x1D, 0x4E, 0xD8),
                        config: Color(0x54, 0x64, 0x54),
                        markdown: Color(0x70, 0x7A, 0x70),
                        markup: Color(0xB4, 0x53, 0x09),
                        style: Color(0x0F, 0x76, 0x6E),
                        browserChromeBackground: Color(0xEE, 0xF3, 0xEC),
                        browserChromeRaised: Color(0xF7, 0xFB, 0xF6),
                        browserChromeField: Color(0xFB, 0xFD, 0xF9),
                        browserChromeDivider: Color(0xC1, 0xCC, 0xC0),
                        browserChromeFieldBorder: Color(0xA9, 0xB9, 0xA7),
                        browserChromeTabBackground: Color(0xF9, 0xFB, 0xF7),
                        browserChromeTabBorder: Color(0x7D, 0x97, 0x7F),
                        browserChromeTabForeground: Color(0x1C, 0x24, 0x1D),
                        browserChromeTabInactiveForeground: Color(0x5A, 0x6A, 0x5B),
                        browserChromeGlyph: Color(0x44, 0x64, 0x46),
                        tabBackground: Color(0xF8, 0xFB, 0xF6),
                        tabHeaderBackground: Color(0xEC, 0xF2, 0xEA),
                        tabHeaderSelected: Color(0xF8, 0xFB, 0xF6),
                        tabHeaderPointerOver: Color(0xE5, 0xED, 0xE3),
                        tabHeaderPressed: Color(0xD9, 0xE5, 0xD6),
                        tabHeaderDisabled: Color(0xEC, 0xF2, 0xEA),
                        tabHeaderForeground: Color(0x60, 0x70, 0x60),
                        tabHeaderForegroundSelected: Color(0x1C, 0x24, 0x1D),
                        tabHeaderForegroundDisabled: Color(0xA0, 0xAB, 0xA0),
                        tabButtonForeground: Color(0x2F, 0x6F, 0x52),
                        tabButtonForegroundPressed: Color(0x3C, 0x8B, 0x66),
                        tabButtonForegroundDisabled: Color(0xA0, 0xAB, 0xA0),
                        tabScrollForeground: Color(0x59, 0x69, 0x59),
                        tabScrollForegroundPressed: Color(0x1C, 0x24, 0x1D),
                        tabScrollForegroundDisabled: Color(0xA0, 0xAB, 0xA0),
                        chromeBackground: Color(0xF4, 0xF7, 0xF2),
                        chromeHoverBackground: Color(0xEA, 0xEF, 0xE8),
                        chromePressedBackground: Color(0xDF, 0xE8, 0xDC),
                        chromeForeground: Color(0x1C, 0x24, 0x1D),
                        chromeInactiveForeground: Color(0x7C, 0x87, 0x7D)),
                    CreatePalette(
                        pageBackground: Color(0x0D, 0x11, 0x0D),
                        paneBackground: Color(0x0D, 0x11, 0x0D),
                        surfaceBackground: Color(0x12, 0x17, 0x12),
                        mutedSurface: Color(0x17, 0x20, 0x19),
                        brandMarkBackground: Color(0x14, 0x19, 0x14),
                        brandMarkBorder: Color(0x22, 0x30, 0x24),
                        border: Color(0x22, 0x30, 0x24),
                        paneDivider: Color(0x1B, 0x24, 0x1C),
                        paneActiveBorder: Color(0xC8, 0xD5, 0xC8),
                        navHover: Color(0x15, 0x20, 0x18),
                        navActive: Color(0x19, 0x24, 0x1C),
                        textPrimary: Color(0xF0, 0xF5, 0xEF),
                        textSecondary: Color(0xA5, 0xB4, 0xA5),
                        textTertiary: Color(0x7A, 0x88, 0x7A),
                        success: Color(0x4B, 0xD1, 0x7F),
                        warning: Color(0xE0, 0xB8, 0x5D),
                        danger: Color(0xEF, 0x8A, 0x84),
                        info: Color(0x7F, 0xB0, 0xE6),
                        terminal: Color(0x41, 0xD3, 0xC1),
                        csharp: Color(0x69, 0xD3, 0x91),
                        typeScript: Color(0x6C, 0xA7, 0xD1),
                        javaScript: Color(0xE6, 0xC4, 0x63),
                        script: Color(0x8A, 0xB6, 0xF5),
                        config: Color(0xB2, 0xC0, 0xB1),
                        markdown: Color(0xA6, 0xB0, 0xA5),
                        markup: Color(0xF0, 0xA1, 0x69),
                        style: Color(0x56, 0xD0, 0xBC),
                        browserChromeBackground: Color(0x11, 0x18, 0x12),
                        browserChromeRaised: Color(0x17, 0x20, 0x18),
                        browserChromeField: Color(0x12, 0x19, 0x14),
                        browserChromeDivider: Color(0x2B, 0x3A, 0x2C),
                        browserChromeFieldBorder: Color(0x3C, 0x50, 0x40),
                        browserChromeTabBackground: Color(0x19, 0x23, 0x1B),
                        browserChromeTabBorder: Color(0x7A, 0x97, 0x7A),
                        browserChromeTabForeground: Color(0xF0, 0xF5, 0xEF),
                        browserChromeTabInactiveForeground: Color(0x96, 0xA7, 0x96),
                        browserChromeGlyph: Color(0xC5, 0xD1, 0xC5),
                        tabBackground: Color(0x12, 0x17, 0x12),
                        tabHeaderBackground: Color(0x12, 0x17, 0x12),
                        tabHeaderSelected: Color(0x17, 0x20, 0x19),
                        tabHeaderPointerOver: Color(0x15, 0x20, 0x18),
                        tabHeaderPressed: Color(0x19, 0x24, 0x1C),
                        tabHeaderDisabled: Color(0x12, 0x17, 0x12),
                        tabHeaderForeground: Color(0xA5, 0xB4, 0xA5),
                        tabHeaderForegroundSelected: Color(0xF0, 0xF5, 0xEF),
                        tabHeaderForegroundDisabled: Color(0x7A, 0x88, 0x7A),
                        tabButtonForeground: Color(0x71, 0xC8, 0x99),
                        tabButtonForegroundPressed: Color(0x98, 0xE0, 0xB9),
                        tabButtonForegroundDisabled: Color(0x7A, 0x88, 0x7A),
                        tabScrollForeground: Color(0xA5, 0xB4, 0xA5),
                        tabScrollForegroundPressed: Color(0xF0, 0xF5, 0xEF),
                        tabScrollForegroundDisabled: Color(0x7A, 0x88, 0x7A),
                        chromeBackground: Color(0x0D, 0x11, 0x0D),
                        chromeHoverBackground: Color(0x15, 0x20, 0x18),
                        chromePressedBackground: Color(0x19, 0x24, 0x1C),
                        chromeForeground: Color(0xF0, 0xF5, 0xEF),
                        chromeInactiveForeground: Color(0x7A, 0x88, 0x7A))),
                [ShellThemePackIds.Copper] = new(
                    ShellThemePackIds.Copper,
                    CreatePalette(
                        pageBackground: Color(0xF7, 0xF2, 0xEC),
                        paneBackground: Color(0xF7, 0xF2, 0xEC),
                        surfaceBackground: Color(0xFC, 0xF8, 0xF2),
                        mutedSurface: Color(0xF3, 0xEA, 0xE2),
                        brandMarkBackground: Color(0xFC, 0xF8, 0xF2),
                        brandMarkBorder: Color(0xD7, 0xC6, 0xB8),
                        border: Color(0xCD, 0xB8, 0xA8),
                        paneDivider: Color(0xD8, 0xC7, 0xBA),
                        paneActiveBorder: Color(0x6B, 0x4A, 0x35),
                        navHover: Color(0xEF, 0xE4, 0xDA),
                        navActive: Color(0xE5, 0xD7, 0xCA),
                        textPrimary: Color(0x25, 0x1D, 0x17),
                        textSecondary: Color(0x5B, 0x48, 0x3D),
                        textTertiary: Color(0x7E, 0x67, 0x5A),
                        success: Color(0x17, 0x72, 0x45),
                        warning: Color(0xB7, 0x79, 0x1F),
                        danger: Color(0xB2, 0x3A, 0x2F),
                        info: Color(0x2C, 0x6B, 0xAA),
                        terminal: Color(0x0F, 0x76, 0x6E),
                        csharp: Color(0x23, 0x7A, 0x57),
                        typeScript: Color(0x2E, 0x6C, 0x93),
                        javaScript: Color(0x9A, 0x67, 0x00),
                        script: Color(0x24, 0x58, 0xA6),
                        config: Color(0x70, 0x5C, 0x50),
                        markdown: Color(0x7A, 0x6C, 0x62),
                        markup: Color(0xB8, 0x5C, 0x2A),
                        style: Color(0x0F, 0x76, 0x6E),
                        browserChromeBackground: Color(0xF3, 0xEA, 0xE2),
                        browserChromeRaised: Color(0xFC, 0xF8, 0xF2),
                        browserChromeField: Color(0xFF, 0xFD, 0xFC),
                        browserChromeDivider: Color(0xD7, 0xC3, 0xB5),
                        browserChromeFieldBorder: Color(0xBF, 0xA1, 0x8F),
                        browserChromeTabBackground: Color(0xFC, 0xF7, 0xF1),
                        browserChromeTabBorder: Color(0xA7, 0x82, 0x69),
                        browserChromeTabForeground: Color(0x25, 0x1D, 0x17),
                        browserChromeTabInactiveForeground: Color(0x70, 0x5C, 0x50),
                        browserChromeGlyph: Color(0x7E, 0x67, 0x5A),
                        tabBackground: Color(0xFC, 0xF8, 0xF2),
                        tabHeaderBackground: Color(0xF2, 0xE8, 0xDD),
                        tabHeaderSelected: Color(0xFC, 0xF8, 0xF2),
                        tabHeaderPointerOver: Color(0xEA, 0xDF, 0xD4),
                        tabHeaderPressed: Color(0xE0, 0xD1, 0xC3),
                        tabHeaderDisabled: Color(0xF2, 0xE8, 0xDD),
                        tabHeaderForeground: Color(0x6D, 0x5A, 0x4C),
                        tabHeaderForegroundSelected: Color(0x25, 0x1D, 0x17),
                        tabHeaderForegroundDisabled: Color(0xA6, 0x93, 0x86),
                        tabButtonForeground: Color(0x9A, 0x58, 0x2F),
                        tabButtonForegroundPressed: Color(0xB6, 0x6A, 0x34),
                        tabButtonForegroundDisabled: Color(0xA6, 0x93, 0x86),
                        tabScrollForeground: Color(0x68, 0x55, 0x48),
                        tabScrollForegroundPressed: Color(0x25, 0x1D, 0x17),
                        tabScrollForegroundDisabled: Color(0xA6, 0x93, 0x86),
                        chromeBackground: Color(0xF8, 0xF3, 0xED),
                        chromeHoverBackground: Color(0xF0, 0xE7, 0xDD),
                        chromePressedBackground: Color(0xE6, 0xDA, 0xCC),
                        chromeForeground: Color(0x25, 0x1D, 0x17),
                        chromeInactiveForeground: Color(0x8A, 0x76, 0x6B)),
                    CreatePalette(
                        pageBackground: Color(0x12, 0x0E, 0x0B),
                        paneBackground: Color(0x12, 0x0E, 0x0B),
                        surfaceBackground: Color(0x18, 0x13, 0x10),
                        mutedSurface: Color(0x21, 0x19, 0x15),
                        brandMarkBackground: Color(0x1A, 0x14, 0x11),
                        brandMarkBorder: Color(0x34, 0x28, 0x21),
                        border: Color(0x33, 0x27, 0x20),
                        paneDivider: Color(0x26, 0x1D, 0x18),
                        paneActiveBorder: Color(0xE6, 0xD2, 0xC6),
                        navHover: Color(0x20, 0x18, 0x14),
                        navActive: Color(0x26, 0x1D, 0x18),
                        textPrimary: Color(0xF8, 0xF0, 0xEA),
                        textSecondary: Color(0xC3, 0xAE, 0xA2),
                        textTertiary: Color(0x99, 0x81, 0x73),
                        success: Color(0x49, 0xC7, 0x8B),
                        warning: Color(0xE0, 0xB1, 0x5F),
                        danger: Color(0xF0, 0x91, 0x84),
                        info: Color(0x8C, 0xC4, 0xFF),
                        terminal: Color(0x4D, 0xD3, 0xC5),
                        csharp: Color(0x66, 0xD3, 0xA0),
                        typeScript: Color(0x74, 0xB7, 0xDB),
                        javaScript: Color(0xE7, 0xC5, 0x6B),
                        script: Color(0x8D, 0xB2, 0xF3),
                        config: Color(0xC7, 0xB3, 0xA8),
                        markdown: Color(0xB4, 0xA4, 0x9C),
                        markup: Color(0xF0, 0xA8, 0x70),
                        style: Color(0x58, 0xD1, 0xC1),
                        browserChromeBackground: Color(0x18, 0x12, 0x0F),
                        browserChromeRaised: Color(0x21, 0x19, 0x15),
                        browserChromeField: Color(0x1A, 0x14, 0x11),
                        browserChromeDivider: Color(0x3A, 0x2C, 0x24),
                        browserChromeFieldBorder: Color(0x4E, 0x3B, 0x31),
                        browserChromeTabBackground: Color(0x24, 0x1B, 0x17),
                        browserChromeTabBorder: Color(0xA7, 0x81, 0x68),
                        browserChromeTabForeground: Color(0xF8, 0xF0, 0xEA),
                        browserChromeTabInactiveForeground: Color(0xB7, 0x9F, 0x92),
                        browserChromeGlyph: Color(0xD8, 0xC6, 0xBC),
                        tabBackground: Color(0x18, 0x13, 0x10),
                        tabHeaderBackground: Color(0x18, 0x13, 0x10),
                        tabHeaderSelected: Color(0x21, 0x19, 0x15),
                        tabHeaderPointerOver: Color(0x20, 0x18, 0x14),
                        tabHeaderPressed: Color(0x26, 0x1D, 0x18),
                        tabHeaderDisabled: Color(0x18, 0x13, 0x10),
                        tabHeaderForeground: Color(0xC3, 0xAE, 0xA2),
                        tabHeaderForegroundSelected: Color(0xF8, 0xF0, 0xEA),
                        tabHeaderForegroundDisabled: Color(0x99, 0x81, 0x73),
                        tabButtonForeground: Color(0xC8, 0x87, 0x58),
                        tabButtonForegroundPressed: Color(0xE0, 0xA0, 0x6F),
                        tabButtonForegroundDisabled: Color(0x99, 0x81, 0x73),
                        tabScrollForeground: Color(0xC3, 0xAE, 0xA2),
                        tabScrollForegroundPressed: Color(0xF8, 0xF0, 0xEA),
                        tabScrollForegroundDisabled: Color(0x99, 0x81, 0x73),
                        chromeBackground: Color(0x12, 0x0E, 0x0B),
                        chromeHoverBackground: Color(0x20, 0x18, 0x14),
                        chromePressedBackground: Color(0x26, 0x1D, 0x18),
                        chromeForeground: Color(0xF8, 0xF0, 0xEA),
                        chromeInactiveForeground: Color(0x99, 0x81, 0x73))),
            };
        }

        private static IReadOnlyDictionary<string, Windows.UI.Color> CreatePalette(
            Windows.UI.Color pageBackground,
            Windows.UI.Color paneBackground,
            Windows.UI.Color surfaceBackground,
            Windows.UI.Color mutedSurface,
            Windows.UI.Color brandMarkBackground,
            Windows.UI.Color brandMarkBorder,
            Windows.UI.Color border,
            Windows.UI.Color paneDivider,
            Windows.UI.Color paneActiveBorder,
            Windows.UI.Color navHover,
            Windows.UI.Color navActive,
            Windows.UI.Color textPrimary,
            Windows.UI.Color textSecondary,
            Windows.UI.Color textTertiary,
            Windows.UI.Color success,
            Windows.UI.Color warning,
            Windows.UI.Color danger,
            Windows.UI.Color info,
            Windows.UI.Color terminal,
            Windows.UI.Color csharp,
            Windows.UI.Color typeScript,
            Windows.UI.Color javaScript,
            Windows.UI.Color script,
            Windows.UI.Color config,
            Windows.UI.Color markdown,
            Windows.UI.Color markup,
            Windows.UI.Color style,
            Windows.UI.Color browserChromeBackground,
            Windows.UI.Color browserChromeRaised,
            Windows.UI.Color browserChromeField,
            Windows.UI.Color browserChromeDivider,
            Windows.UI.Color browserChromeFieldBorder,
            Windows.UI.Color browserChromeTabBackground,
            Windows.UI.Color browserChromeTabBorder,
            Windows.UI.Color browserChromeTabForeground,
            Windows.UI.Color browserChromeTabInactiveForeground,
            Windows.UI.Color browserChromeGlyph,
            Windows.UI.Color tabBackground,
            Windows.UI.Color tabHeaderBackground,
            Windows.UI.Color tabHeaderSelected,
            Windows.UI.Color tabHeaderPointerOver,
            Windows.UI.Color tabHeaderPressed,
            Windows.UI.Color tabHeaderDisabled,
            Windows.UI.Color tabHeaderForeground,
            Windows.UI.Color tabHeaderForegroundSelected,
            Windows.UI.Color tabHeaderForegroundDisabled,
            Windows.UI.Color tabButtonForeground,
            Windows.UI.Color tabButtonForegroundPressed,
            Windows.UI.Color tabButtonForegroundDisabled,
            Windows.UI.Color tabScrollForeground,
            Windows.UI.Color tabScrollForegroundPressed,
            Windows.UI.Color tabScrollForegroundDisabled,
            Windows.UI.Color chromeBackground,
            Windows.UI.Color chromeHoverBackground,
            Windows.UI.Color chromePressedBackground,
            Windows.UI.Color chromeForeground,
            Windows.UI.Color chromeInactiveForeground)
        {
            return new Dictionary<string, Windows.UI.Color>(StringComparer.Ordinal)
            {
                ["ShellPageBackgroundBrush"] = pageBackground,
                ["ShellPaneBackgroundBrush"] = paneBackground,
                ["ShellSurfaceBackgroundBrush"] = surfaceBackground,
                ["ShellMutedSurfaceBrush"] = mutedSurface,
                ["ShellBrandMarkBackgroundBrush"] = brandMarkBackground,
                ["ShellBrandMarkBorderBrush"] = brandMarkBorder,
                ["ShellBorderBrush"] = border,
                ["ShellPaneDividerBrush"] = paneDivider,
                ["ShellPaneActiveBorderBrush"] = paneActiveBorder,
                ["ShellNavHoverBrush"] = navHover,
                ["ShellNavActiveBrush"] = navActive,
                ["ShellTextPrimaryBrush"] = textPrimary,
                ["ShellTextSecondaryBrush"] = textSecondary,
                ["ShellTextTertiaryBrush"] = textTertiary,
                ["ShellSuccessBrush"] = success,
                ["ShellWarningBrush"] = warning,
                ["ShellDangerBrush"] = danger,
                ["ShellInfoBrush"] = info,
                ["ShellTerminalBrush"] = terminal,
                ["ShellCSharpBrush"] = csharp,
                ["ShellTypeScriptBrush"] = typeScript,
                ["ShellJavaScriptBrush"] = javaScript,
                ["ShellScriptBrush"] = script,
                ["ShellConfigBrush"] = config,
                ["ShellMarkdownBrush"] = markdown,
                ["ShellMarkupBrush"] = markup,
                ["ShellStyleBrush"] = style,
                ["BrowserChromeBackgroundBrush"] = browserChromeBackground,
                ["BrowserChromeRaisedBrush"] = browserChromeRaised,
                ["BrowserChromeFieldBrush"] = browserChromeField,
                ["BrowserChromeDividerBrush"] = browserChromeDivider,
                ["BrowserChromeFieldBorderBrush"] = browserChromeFieldBorder,
                ["BrowserChromeTabBackgroundBrush"] = browserChromeTabBackground,
                ["BrowserChromeTabBorderBrush"] = browserChromeTabBorder,
                ["BrowserChromeTabForegroundBrush"] = browserChromeTabForeground,
                ["BrowserChromeTabInactiveForegroundBrush"] = browserChromeTabInactiveForeground,
                ["BrowserChromeGlyphBrush"] = browserChromeGlyph,
                ["TabViewBackground"] = tabBackground,
                ["TabViewItemHeaderBackground"] = tabHeaderBackground,
                ["TabViewItemHeaderBackgroundSelected"] = tabHeaderSelected,
                ["TabViewItemHeaderBackgroundPointerOver"] = tabHeaderPointerOver,
                ["TabViewItemHeaderBackgroundPressed"] = tabHeaderPressed,
                ["TabViewItemHeaderBackgroundDisabled"] = tabHeaderDisabled,
                ["TabViewItemHeaderForeground"] = tabHeaderForeground,
                ["TabViewItemHeaderForegroundPressed"] = tabHeaderForegroundSelected,
                ["TabViewItemHeaderForegroundSelected"] = tabHeaderForegroundSelected,
                ["TabViewItemHeaderForegroundPointerOver"] = tabHeaderForegroundSelected,
                ["TabViewItemHeaderForegroundDisabled"] = tabHeaderForegroundDisabled,
                ["TabViewItemIconForegroundSelected"] = tabHeaderForegroundSelected,
                ["TabViewItemHeaderSelectedCloseButtonBackground"] = WithAlpha(tabHeaderSelected, 0x00),
                ["TabViewItemHeaderSelectedCloseButtonForeground"] = tabHeaderForegroundSelected,
                ["TabViewItemHeaderCloseButtonBorderBrushSelected"] = WithAlpha(tabHeaderSelected, 0x00),
                ["TabViewButtonBackground"] = tabBackground,
                ["TabViewButtonBackgroundPressed"] = tabHeaderPressed,
                ["TabViewButtonBackgroundPointerOver"] = tabHeaderPointerOver,
                ["TabViewButtonForeground"] = tabButtonForeground,
                ["TabViewButtonForegroundPressed"] = tabButtonForegroundPressed,
                ["TabViewButtonForegroundPointerOver"] = tabButtonForegroundPressed,
                ["TabViewButtonForegroundDisabled"] = tabButtonForegroundDisabled,
                ["TabViewButtonForegroundActiveTab"] = tabButtonForeground,
                ["TabViewScrollButtonBackground"] = tabBackground,
                ["TabViewScrollButtonBackgroundPressed"] = tabHeaderPressed,
                ["TabViewScrollButtonBackgroundPointerOver"] = tabHeaderPointerOver,
                ["TabViewScrollButtonForeground"] = tabScrollForeground,
                ["TabViewScrollButtonForegroundPressed"] = tabScrollForegroundPressed,
                ["TabViewScrollButtonForegroundPointerOver"] = tabScrollForegroundPressed,
                ["TabViewScrollButtonForegroundDisabled"] = tabScrollForegroundDisabled,
                ["ShellChromeBackgroundColor"] = chromeBackground,
                ["ShellChromeHoverBackgroundColor"] = chromeHoverBackground,
                ["ShellChromePressedBackgroundColor"] = chromePressedBackground,
                ["ShellChromeForegroundColor"] = chromeForeground,
                ["ShellChromeInactiveForegroundColor"] = chromeInactiveForeground,
            };
        }

        private static void ApplyPaletteToThemeDictionary(ResourceDictionary themeDictionary, IReadOnlyDictionary<string, Windows.UI.Color> palette)
        {
            if (themeDictionary is null)
            {
                return;
            }

            foreach (KeyValuePair<string, Windows.UI.Color> entry in palette)
            {
                if (!themeDictionary.TryGetValue(entry.Key, out object resource))
                {
                    continue;
                }

                if (resource is SolidColorBrush brush)
                {
                    brush.Color = entry.Value;
                }
                else
                {
                    themeDictionary[entry.Key] = new SolidColorBrush(entry.Value);
                }
            }
        }

        private static ResourceDictionary ResolveThemeDictionary(ResourceDictionary dictionary, string themeKey)
        {
            if (TryGetThemeDictionary(dictionary, themeKey, out ResourceDictionary themeDictionary))
            {
                return themeDictionary;
            }

            if (dictionary?.MergedDictionaries is null)
            {
                return null;
            }

            foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            {
                if (TryGetThemeDictionary(merged, themeKey, out themeDictionary))
                {
                    return themeDictionary;
                }
            }

            return null;
        }

        private static bool TryGetThemeDictionary(ResourceDictionary dictionary, string themeKey, out ResourceDictionary themeDictionary)
        {
            if (dictionary?.ThemeDictionaries?.TryGetValue(themeKey, out object value) == true && value is ResourceDictionary resolved)
            {
                themeDictionary = resolved;
                return true;
            }

            themeDictionary = null;
            return false;
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

        private static ShellThemePack ResolvePack(string packId)
        {
            string normalizedPackId = string.IsNullOrWhiteSpace(packId)
                ? DefaultPackId
                : packId.Trim();
            return Packs.TryGetValue(normalizedPackId, out ShellThemePack pack)
                ? pack
                : Packs[DefaultPackId];
        }

        private static Windows.UI.Color Color(byte red, byte green, byte blue)
        {
            return Windows.UI.Color.FromArgb(0xFF, red, green, blue);
        }

        private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
        {
            return Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private sealed class ShellThemePack
        {
            public ShellThemePack(string id, IReadOnlyDictionary<string, Windows.UI.Color> lightPalette, IReadOnlyDictionary<string, Windows.UI.Color> darkPalette)
            {
                Id = id;
                LightPalette = lightPalette;
                DarkPalette = darkPalette;
            }

            public string Id { get; }

            public IReadOnlyDictionary<string, Windows.UI.Color> LightPalette { get; }

            public IReadOnlyDictionary<string, Windows.UI.Color> DarkPalette { get; }

            public IReadOnlyDictionary<string, Windows.UI.Color> ResolvePalette(ElementTheme theme)
            {
                return theme == ElementTheme.Light ? LightPalette : DarkPalette;
            }
        }
    }
}
