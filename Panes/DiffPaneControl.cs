using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Git;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Foundation;

namespace SelfContainedDeployment.Panes
{
    internal sealed class DiffPaneRenderSnapshot
    {
        public string Path { get; set; }

        public string Summary { get; set; }

        public string RawText { get; set; }

        public int LineCount { get; set; }

        public List<DiffPaneRenderLine> Lines { get; set; } = new();
    }

    internal sealed class DiffPaneRenderLine
    {
        public int Index { get; set; }

        public string Kind { get; set; }

        public string Text { get; set; }

        public string Foreground { get; set; }
    }

    public sealed class DiffPaneControl : UserControl
    {
        private static readonly Regex HunkHeaderRegex = new(@"@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.Compiled);
        private readonly Grid _root;
        private readonly Border _headerBorder;
        private readonly TextBlock _pathText;
        private readonly TextBlock _summaryText;
        private readonly Grid _diffLinesPanel;
        private readonly ScrollViewer _scrollViewer;
        private readonly Dictionary<string, FrameworkElement> _sectionAnchors = new(StringComparer.Ordinal);
        private string _currentPath;
        private string _currentDiff;
        private IReadOnlyList<GitChangedFile> _currentFiles = Array.Empty<GitChangedFile>();

        public DiffPaneControl()
        {
            _root = new Grid();
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _headerBorder = new Border
            {
                BorderBrush = ResolveBrush("ShellBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 10, 12, 10),
                Background = ResolveBrush("ShellSurfaceBackgroundBrush"),
            };

            StackPanel headerStack = new()
            {
                Spacing = 2,
            };

            _pathText = new TextBlock
            {
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResolveBrush("ShellTextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            headerStack.Children.Add(_pathText);

            _summaryText = new TextBlock
            {
                FontSize = 11,
                Foreground = ResolveBrush("ShellTextTertiaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            headerStack.Children.Add(_summaryText);

            _headerBorder.Child = headerStack;
            _root.Children.Add(_headerBorder);

            _diffLinesPanel = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _diffLinesPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12, 12, 12, 12),
                Content = _diffLinesPanel,
                Background = ResolveBrush("ShellSurfaceBackgroundBrush"),
            };
            _scrollViewer.SizeChanged += (_, _) => UpdateDiffContentWidth();
            Grid.SetRow(_scrollViewer, 1);
            _root.Children.Add(_scrollViewer);

            Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            Content = _root;

            PointerPressed += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);
            GotFocus += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler InteractionRequested;

        public string CurrentPath => _currentPath;

        public string CurrentDiff => _currentDiff;

        public void ApplyAutomationIdentity(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            AutomationProperties.SetAutomationId(this, $"shell-diff-pane-{paneId}");
            AutomationProperties.SetName(this, "Patch review pane");
            AutomationProperties.SetAutomationId(_headerBorder, $"shell-diff-pane-header-{paneId}");
            AutomationProperties.SetName(_headerBorder, "Patch review header");
            AutomationProperties.SetAutomationId(_pathText, $"shell-diff-pane-path-{paneId}");
            AutomationProperties.SetName(_pathText, "Patch review path");
            AutomationProperties.SetAutomationId(_summaryText, $"shell-diff-pane-summary-{paneId}");
            AutomationProperties.SetName(_summaryText, "Patch review summary");
            AutomationProperties.SetAutomationId(_scrollViewer, $"shell-diff-pane-scroll-{paneId}");
            AutomationProperties.SetName(_scrollViewer, "Patch review scroll");
            AutomationProperties.SetAutomationId(_diffLinesPanel, $"shell-diff-pane-content-{paneId}");
            AutomationProperties.SetName(_diffLinesPanel, "Patch review content");
        }

        public void ApplyTheme(ElementTheme theme)
        {
            RequestedTheme = theme;
            Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            _headerBorder.Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            _headerBorder.BorderBrush = ResolveBrush("ShellBorderBrush");
            _pathText.Foreground = ResolveBrush("ShellTextPrimaryBrush");
            _summaryText.Foreground = ResolveBrush("ShellTextTertiaryBrush");
            _scrollViewer.Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            if (_currentFiles.Count > 1)
            {
                RenderDiffSet(_currentFiles, _currentPath);
                return;
            }

            RenderDiff(_currentDiff);
        }

        public void FocusPane()
        {
            _scrollViewer.Focus(FocusState.Programmatic);
        }

        public void RequestLayout()
        {
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();
            UpdateDiffContentWidth();
        }

        public void SetDiff(string path, string diffText)
        {
            _currentFiles = Array.Empty<GitChangedFile>();
            _currentPath = path ?? string.Empty;
            _currentDiff = diffText ?? string.Empty;

            _pathText.Text = string.IsNullOrWhiteSpace(path) ? "No diff selected" : path.Replace('\\', '/');
            _summaryText.Text = string.IsNullOrWhiteSpace(path)
                ? "Choose a changed file from the inspector."
                : "Patch view";

            RenderDiff(diffText);
        }

        internal void SetDiffSet(IReadOnlyList<GitChangedFile> files, string selectedPath)
        {
            List<GitChangedFile> diffFiles = files?
                .Where(file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
                .Select(file => new GitChangedFile
                {
                    Status = file.Status,
                    Path = file.Path,
                    AddedLines = file.AddedLines,
                    RemovedLines = file.RemovedLines,
                    DiffText = file.DiffText,
                })
                .ToList()
                ?? new List<GitChangedFile>();

            if (diffFiles.Count <= 1)
            {
                GitChangedFile singleFile = diffFiles.FirstOrDefault();
                SetDiff(singleFile?.Path ?? selectedPath, singleFile?.DiffText);
                return;
            }

            _currentFiles = diffFiles;
            _currentPath = selectedPath ?? string.Empty;
            _currentDiff = BuildCombinedDiffText(diffFiles);
            _pathText.Text = string.IsNullOrWhiteSpace(selectedPath)
                ? "All changed files"
                : selectedPath.Replace('\\', '/');
            _summaryText.Text = $"{diffFiles.Count} files · Scroll to review the full patch";
            RenderDiffSet(diffFiles, selectedPath);
        }

        internal DiffPaneRenderSnapshot GetRenderSnapshot(int maxLines = 0)
        {
            string rawText = _currentFiles.Count > 1
                ? BuildCombinedDiffText(_currentFiles)
                : _currentDiff ?? string.Empty;
            string[] allLines = string.IsNullOrEmpty(rawText)
                ? Array.Empty<string>()
                : rawText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            IEnumerable<(string Line, int Index)> selectedLines = allLines
                .Select((line, index) => (line, index));
            if (maxLines > 0)
            {
                selectedLines = selectedLines.Take(maxLines);
            }

            return new DiffPaneRenderSnapshot
            {
                Path = _currentPath ?? string.Empty,
                Summary = _summaryText.Text ?? string.Empty,
                RawText = rawText,
                LineCount = allLines.Length,
                Lines = selectedLines.Select(entry =>
                {
                    string kind = ClassifyDiffLine(entry.Line);
                    return new DiffPaneRenderLine
                    {
                        Index = entry.Index,
                        Kind = kind,
                        Text = entry.Line,
                        Foreground = SerializeBrush(ResolveBrush(ResolveDiffLineBrushKey(kind))),
                    };
                }).ToList(),
            };
        }

        private void RenderDiff(string diffText)
        {
            _sectionAnchors.Clear();
            _diffLinesPanel.Children.Clear();
            _diffLinesPanel.RowDefinitions.Clear();
            if (string.IsNullOrWhiteSpace(diffText))
            {
                if (!string.IsNullOrWhiteSpace(_currentPath))
                {
                    return;
                }

                TextBlock emptyText = new()
                {
                    Text = "Select a changed file to inspect its patch.",
                    Foreground = ResolveBrush("ShellTextTertiaryBrush"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                };
                _diffLinesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(emptyText, 0);
                _diffLinesPanel.Children.Add(emptyText);
                return;
            }

            string[] lines = diffText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            RenderDiffLines(lines, 0);
            UpdateDiffContentWidth();
        }

        private void RenderDiffSet(IReadOnlyList<GitChangedFile> files, string selectedPath)
        {
            _sectionAnchors.Clear();
            _diffLinesPanel.Children.Clear();
            _diffLinesPanel.RowDefinitions.Clear();

            if (files is null || files.Count == 0)
            {
                RenderDiff(null);
                return;
            }

            int rowIndex = 0;
            foreach (GitChangedFile file in files)
            {
                Border sectionHeader = BuildSectionHeader(file, string.Equals(file.Path, selectedPath, StringComparison.Ordinal));
                _sectionAnchors[file.Path] = sectionHeader;
                AddDiffRow(sectionHeader, rowIndex++);

                string[] lines = string.IsNullOrWhiteSpace(file.DiffText)
                    ? new[] { "Patch unavailable for this file." }
                    : file.DiffText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                rowIndex = RenderDiffLines(lines, rowIndex);
            }

            UpdateDiffContentWidth();
            ScrollToPath(selectedPath);
        }

        private int RenderDiffLines(IReadOnlyList<string> lines, int startingRow)
        {
            int oldLineNumber = 0;
            int newLineNumber = 0;
            bool hasActiveHunk = false;
            int nextRow = startingRow;

            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                string kind = ClassifyDiffLine(line);
                int? leftNumber = null;
                int? rightNumber = null;

                if (kind == "hunk")
                {
                    hasActiveHunk = TryParseHunkHeader(line, out oldLineNumber, out newLineNumber);
                }
                else if (kind == "deletion")
                {
                    if (hasActiveHunk)
                    {
                        leftNumber = oldLineNumber++;
                    }
                }
                else if (kind == "addition")
                {
                    if (hasActiveHunk)
                    {
                        rightNumber = newLineNumber++;
                    }
                }
                else if (kind == "context")
                {
                    if (hasActiveHunk)
                    {
                        leftNumber = oldLineNumber++;
                        rightNumber = newLineNumber++;
                    }
                }

                FrameworkElement row = BuildDiffLineRow(leftNumber, rightNumber, line, kind);
                AddDiffRow(row, nextRow++);
            }

            return nextRow;
        }

        private void AddDiffRow(FrameworkElement row, int rowIndex)
        {
            _diffLinesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(row, rowIndex);
            _diffLinesPanel.Children.Add(row);
        }

        private Border BuildSectionHeader(GitChangedFile file, bool selected)
        {
            Border header = new()
            {
                Background = ResolveBrush(selected ? "ShellMutedSurfaceBrush" : "ShellSurfaceBackgroundBrush"),
                BorderBrush = ResolveBrush(selected ? "ShellPaneActiveBorderBrush" : "ShellBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 8, 8, 6),
                Margin = new Thickness(0, 10, 0, 2),
            };

            Grid layout = new()
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                ColumnSpacing = 10,
            };

            TextBlock title = new()
            {
                Text = file.DisplayName,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResolveBrush("ShellTextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            layout.Children.Add(title);

            TextBlock stats = new()
            {
                Text = $"+{file.AddedLines}  -{file.RemovedLines}",
                FontSize = 11,
                Foreground = ResolveBrush("ShellTextTertiaryBrush"),
                HorizontalTextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(stats, 1);
            layout.Children.Add(stats);

            header.Child = layout;
            return header;
        }

        private void ScrollToPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !_sectionAnchors.TryGetValue(path, out FrameworkElement anchor))
            {
                return;
            }

            DispatcherQueue?.TryEnqueue(() =>
            {
                _scrollViewer.UpdateLayout();
                anchor.UpdateLayout();
                Point point = anchor.TransformToVisual(_diffLinesPanel).TransformPoint(new Point(0, 0));
                double offset = Math.Max(0, point.Y - 12);
                _scrollViewer.ChangeView(null, offset, null, true);
            });
        }

        private static string BuildCombinedDiffText(IReadOnlyList<GitChangedFile> files)
        {
            if (files is null || files.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                Environment.NewLine + Environment.NewLine,
                files.Select(file =>
                {
                    string diffText = string.IsNullOrWhiteSpace(file?.DiffText)
                        ? "Patch unavailable for this file."
                        : file.DiffText.TrimEnd();
                    return diffText;
                }));
        }

        private static string ResolveDiffLineBrushKey(string kind)
        {
            return kind switch
            {
                "addition" => "ShellSuccessBrush",
                "deletion" => "ShellDangerBrush",
                "hunk" => "ShellInfoBrush",
                "metadata" => "ShellTextSecondaryBrush",
                "empty" => "ShellTextSecondaryBrush",
                _ => "ShellTextPrimaryBrush",
            };
        }

        private FrameworkElement BuildDiffLineRow(int? oldLineNumber, int? newLineNumber, string line, string kind)
        {
            Grid row = new()
            {
                ColumnSpacing = 10,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0,
                Background = ResolveLineBackground(kind),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock oldNumberText = BuildLineNumberText(oldLineNumber);
            Grid.SetColumn(oldNumberText, 0);
            row.Children.Add(oldNumberText);

            TextBlock newNumberText = BuildLineNumberText(newLineNumber);
            Grid.SetColumn(newNumberText, 1);
            row.Children.Add(newNumberText);

            TextBlock contentText = new()
            {
                Text = string.IsNullOrEmpty(line) ? " " : line,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = ResolveBrush(ResolveDiffLineBrushKey(kind)),
                Margin = new Thickness(0),
            };

            if (kind is "metadata" or "hunk")
            {
                contentText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            Grid.SetColumn(contentText, 2);
            row.Children.Add(contentText);
            return row;
        }

        private void UpdateDiffContentWidth()
        {
            double width = _scrollViewer.ActualWidth - _scrollViewer.Padding.Left - _scrollViewer.Padding.Right;
            _diffLinesPanel.MinWidth = width > 0 ? width : 0;
        }

        private TextBlock BuildLineNumberText(int? lineNumber)
        {
            return new TextBlock
            {
                Text = lineNumber?.ToString() ?? string.Empty,
                Width = 44,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                HorizontalTextAlignment = TextAlignment.Right,
                Foreground = ResolveBrush("ShellTextTertiaryBrush"),
                Opacity = lineNumber.HasValue ? 1 : 0.55,
            };
        }

        private Brush ResolveLineBackground(string kind)
        {
            ElementTheme theme = ResolveEffectiveTheme();
            Windows.UI.Color color = (theme, kind) switch
            {
                (ElementTheme.Light, "addition") => Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xFD, 0xF4),
                (ElementTheme.Light, "deletion") => Windows.UI.Color.FromArgb(0xFF, 0xFE, 0xF2, 0xF2),
                (ElementTheme.Light, "hunk") => Windows.UI.Color.FromArgb(0xFF, 0xEF, 0xF6, 0xFF),
                (ElementTheme.Light, "metadata") => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC),
                (ElementTheme.Dark, "addition") => Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x1E, 0x14),
                (ElementTheme.Dark, "deletion") => Windows.UI.Color.FromArgb(0xFF, 0x26, 0x13, 0x15),
                (ElementTheme.Dark, "hunk") => Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A),
                (ElementTheme.Dark, "metadata") => Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1D),
                _ => Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00),
            };

            return new SolidColorBrush(color);
        }

        private static bool TryParseHunkHeader(string line, out int oldLineNumber, out int newLineNumber)
        {
            oldLineNumber = 0;
            newLineNumber = 0;

            Match match = HunkHeaderRegex.Match(line ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            oldLineNumber = int.Parse(match.Groups["old"].Value);
            newLineNumber = int.Parse(match.Groups["new"].Value);
            return true;
        }

        private static string ClassifyDiffLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return "empty";
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                return "addition";
            }

            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                return "deletion";
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                return "hunk";
            }

            if (line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("\\", StringComparison.Ordinal))
            {
                return "metadata";
            }

            return "context";
        }

        private static ElementTheme ResolveEffectiveTheme()
        {
            return SelfContainedDeployment.SampleConfig.CurrentTheme == ElementTheme.Default
                ? (SelfContainedDeployment.MainPage.Current?.ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark)
                : SelfContainedDeployment.SampleConfig.CurrentTheme;
        }

        private static Brush ResolveBrush(string key)
        {
            ElementTheme effectiveTheme = ResolveEffectiveTheme();

            Windows.UI.Color color = (effectiveTheme, key) switch
            {
                (ElementTheme.Light, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                (ElementTheme.Light, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),
                (ElementTheme.Light, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE7),
                (ElementTheme.Light, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Light, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x18, 0x18, 0x1B),
                (ElementTheme.Light, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x52, 0x52, 0x5B),
                (ElementTheme.Light, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A),
                (ElementTheme.Light, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
                (ElementTheme.Light, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04),
                (ElementTheme.Light, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xDC, 0x26, 0x26),
                (ElementTheme.Light, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Dark, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x11, 0x12, 0x14),
                (ElementTheme.Dark, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0x17, 0x18, 0x1C),
                (ElementTheme.Dark, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x23, 0x25, 0x2B),
                (ElementTheme.Dark, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                (ElementTheme.Dark, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
                (ElementTheme.Dark, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA),
                (ElementTheme.Dark, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A),
                (ElementTheme.Dark, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80),
                (ElementTheme.Dark, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                (ElementTheme.Dark, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
                (ElementTheme.Dark, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                _ => default,
            };

            if (color != default)
            {
                return new SolidColorBrush(color);
            }

            return (Brush)Application.Current.Resources[key];
        }

        private static string SerializeBrush(Brush brush)
        {
            return brush switch
            {
                SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
                null => null,
                _ => brush.GetType().Name,
            };
        }
    }
}
