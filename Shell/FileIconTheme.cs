using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.UI;

namespace SelfContainedDeployment.Shell
{
    internal sealed class FileIconInfo
    {
        public string Glyph { get; init; }

        public FontFamily FontFamily { get; init; }

        public Brush Brush { get; init; }

        public double FontSize { get; init; }
    }

    internal static class FileIconTheme
    {
        private const string ThemeAssetPath = @"Assets\FileIcons\file-icons-icon-theme.json";
        private const double BaseFontSize = 10.5;

        private static readonly Lazy<ThemeData> Theme = new(LoadTheme);

        internal static FileIconInfo Resolve(string relativePath, bool isDirectory)
        {
            return Theme.Value.Resolve(relativePath, isDirectory);
        }

        private static ThemeData LoadTheme()
        {
            try
            {
                string assetPath = Path.Combine(AppContext.BaseDirectory, ThemeAssetPath);
                if (!File.Exists(assetPath))
                {
                    return ThemeData.Empty;
                }

                using FileStream stream = File.OpenRead(assetPath);
                using JsonDocument document = JsonDocument.Parse(stream);

                JsonElement root = document.RootElement;
                Dictionary<string, IconDefinition> iconDefinitions = new(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("iconDefinitions", out JsonElement iconDefinitionsElement))
                {
                    foreach (JsonProperty property in iconDefinitionsElement.EnumerateObject())
                    {
                        if (TryParseIconDefinition(property.Value, out IconDefinition definition))
                        {
                            iconDefinitions[property.Name] = definition;
                        }
                    }
                }

                return new ThemeData(
                    root.TryGetProperty("file", out JsonElement fileElement) ? fileElement.GetString() : null,
                    root.TryGetProperty("folder", out JsonElement folderElement) ? folderElement.GetString() : null,
                    LoadLookup(root, "fileNames"),
                    LoadLookup(root, "fileExtensions"),
                    LoadLookup(root, "folderNames"),
                    iconDefinitions);
            }
            catch
            {
                return ThemeData.Empty;
            }
        }

        private static Dictionary<string, string> LoadLookup(JsonElement root, string propertyName)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty(propertyName, out JsonElement propertyElement))
            {
                return values;
            }

            foreach (JsonProperty property in propertyElement.EnumerateObject())
            {
                string value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values[property.Name] = value;
                }
            }

            return values;
        }

        private static bool TryParseIconDefinition(JsonElement element, out IconDefinition definition)
        {
            definition = null;
            if (!element.TryGetProperty("fontCharacter", out JsonElement characterElement))
            {
                return false;
            }

            string fontCharacter = DecodeFontCharacter(characterElement.GetString());
            if (string.IsNullOrWhiteSpace(fontCharacter))
            {
                return false;
            }

            string fontId = element.TryGetProperty("fontId", out JsonElement fontIdElement)
                ? fontIdElement.GetString()
                : null;

            string fontSizeText = element.TryGetProperty("fontSize", out JsonElement fontSizeElement)
                ? fontSizeElement.GetString()
                : null;

            definition = new IconDefinition
            {
                FontId = fontId,
                Glyph = fontCharacter,
                FontSize = ParseFontSize(fontSizeText),
                Brush = element.TryGetProperty("fontColor", out JsonElement fontColorElement)
                    ? ParseBrush(fontColorElement.GetString())
                    : null,
            };
            return true;
        }

        private static string DecodeFontCharacter(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value[0] != '\\')
            {
                return value;
            }

            if (!int.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
            {
                return null;
            }

            return char.ConvertFromUtf32(codePoint);
        }

        private static double ParseFontSize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return BaseFontSize;
            }

            string normalized = value.Trim();
            if (normalized.EndsWith('%'))
            {
                normalized = normalized[..^1];
            }

            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
                ? BaseFontSize * Math.Clamp(percent / 100d, 0.75d, 1.4d)
                : BaseFontSize;
        }

        private static Brush ParseBrush(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = value.Trim().TrimStart('#');
            if (normalized.Length != 6 || !uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                return null;
            }

            return new SolidColorBrush(Color.FromArgb(
                0xFF,
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF)));
        }

        private sealed class ThemeData
        {
            internal static readonly ThemeData Empty = new(
                defaultFileIconKey: null,
                defaultFolderIconKey: null,
                fileNames: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                fileExtensions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                folderNames: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                iconDefinitions: new Dictionary<string, IconDefinition>(StringComparer.OrdinalIgnoreCase));

            private static readonly Dictionary<string, FontFamily> FontFamilies = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fi"] = new("ms-appx:///Assets/FileIcons/file-icons.ttf#file-icons"),
                ["fa"] = new("ms-appx:///Assets/FileIcons/fontawesome.ttf#FontAwesome"),
                ["mf"] = new("ms-appx:///Assets/FileIcons/mfixx.ttf#MFixx"),
                ["devicons"] = new("ms-appx:///Assets/FileIcons/devopicons.ttf#DevOpicons"),
                ["octicons"] = new("ms-appx:///Assets/FileIcons/octicons.ttf#octicons"),
            };

            private readonly string _defaultFileIconKey;
            private readonly string _defaultFolderIconKey;
            private readonly Dictionary<string, string> _fileNames;
            private readonly Dictionary<string, string> _fileExtensions;
            private readonly Dictionary<string, string> _folderNames;
            private readonly Dictionary<string, IconDefinition> _iconDefinitions;
            private readonly Dictionary<string, FileIconInfo> _resolvedIcons = new(StringComparer.OrdinalIgnoreCase);

            internal ThemeData(
                string defaultFileIconKey,
                string defaultFolderIconKey,
                Dictionary<string, string> fileNames,
                Dictionary<string, string> fileExtensions,
                Dictionary<string, string> folderNames,
                Dictionary<string, IconDefinition> iconDefinitions)
            {
                _defaultFileIconKey = defaultFileIconKey;
                _defaultFolderIconKey = defaultFolderIconKey;
                _fileNames = fileNames;
                _fileExtensions = fileExtensions;
                _folderNames = folderNames;
                _iconDefinitions = iconDefinitions;
            }

            internal FileIconInfo Resolve(string relativePath, bool isDirectory)
            {
                string iconKey = isDirectory
                    ? ResolveFolderIconKey(relativePath)
                    : ResolveFileIconKey(relativePath);

                return ResolveIcon(iconKey);
            }

            private string ResolveFolderIconKey(string relativePath)
            {
                string folderName = Path.GetFileName(relativePath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return !string.IsNullOrWhiteSpace(folderName) && _folderNames.TryGetValue(folderName, out string iconKey)
                    ? iconKey
                    : _defaultFolderIconKey;
            }

            private string ResolveFileIconKey(string relativePath)
            {
                string fileName = Path.GetFileName(relativePath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return _defaultFileIconKey;
                }

                if (_fileNames.TryGetValue(fileName, out string iconKey))
                {
                    return iconKey;
                }

                foreach (string extension in EnumerateExtensionCandidates(fileName))
                {
                    if (_fileExtensions.TryGetValue(extension, out iconKey))
                    {
                        return iconKey;
                    }
                }

                return _defaultFileIconKey;
            }

            private static IEnumerable<string> EnumerateExtensionCandidates(string fileName)
            {
                string[] segments = fileName
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (segments.Length < 2)
                {
                    yield break;
                }

                for (int index = 1; index < segments.Length; index++)
                {
                    yield return string.Join('.', segments.Skip(index));
                }
            }

            private FileIconInfo ResolveIcon(string iconKey)
            {
                if (string.IsNullOrWhiteSpace(iconKey) || !_iconDefinitions.TryGetValue(iconKey, out IconDefinition definition))
                {
                    return null;
                }

                if (_resolvedIcons.TryGetValue(iconKey, out FileIconInfo cached))
                {
                    return cached;
                }

                if (!FontFamilies.TryGetValue(definition.FontId ?? string.Empty, out FontFamily fontFamily))
                {
                    return null;
                }

                FileIconInfo icon = new()
                {
                    Glyph = definition.Glyph,
                    FontFamily = fontFamily,
                    Brush = definition.Brush,
                    FontSize = definition.FontSize,
                };
                _resolvedIcons[iconKey] = icon;
                return icon;
            }
        }

        private sealed class IconDefinition
        {
            public string FontId { get; init; }

            public string Glyph { get; init; }

            public Brush Brush { get; init; }

            public double FontSize { get; init; }
        }
    }
}
