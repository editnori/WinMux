using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Git;

namespace SelfContainedDeployment
{
    public sealed class InspectorDirectoryTreeItem
    {
        public string Name { get; init; }

        public string RelativePath { get; init; }

        public bool IsDirectory { get; init; }

        public string IconGlyph { get; init; }

        public FontFamily IconFontFamily { get; init; }

        public double IconFontSize { get; init; } = 11;

        public bool UseGlyphBadge { get; init; }

        public Brush IconBrush { get; init; }

        public string KindText { get; init; }

        public Brush KindBrush { get; init; }

        public string StatusText { get; init; }

        public Brush StatusBrush { get; init; }

        public Visibility KindVisibility => string.IsNullOrWhiteSpace(KindText) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed class DiffFileListItem
    {
        public GitChangedFile File { get; init; }

        public string AutomationId { get; init; }

        public string AutomationName { get; init; }

        public string StatusSymbol { get; init; }

        public Brush StatusBrush { get; init; }

        public string FileName { get; init; }

        public string MetaText { get; init; }

        public string AddedText { get; init; }

        public Visibility AddedVisibility { get; init; }

        public string RemovedText { get; init; }

        public Visibility RemovedVisibility { get; init; }
    }
}
