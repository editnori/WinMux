using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Git;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

    internal sealed class DiffLineRenderItem
    {
        public int? OldLineNumber { get; set; }

        public int? NewLineNumber { get; set; }

        public string Line { get; set; }

        public string Kind { get; set; }
    }

    internal sealed class DiffRenderCursor
    {
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

        public int NextIndex { get; set; }

        public int OldLineNumber { get; set; }

        public int NewLineNumber { get; set; }

        public bool HasActiveHunk { get; set; }
    }

    public sealed class DiffPaneControl : UserControl
    {
        private static readonly Regex HunkHeaderRegex = new(@"@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.Compiled);
        private const float MinFitWidthZoomFactor = 0.5f;
        private const double DiffCharacterWidth = 7.2;
        private const int InitialDiffRenderLineBatch = 40;
        private const int SubsequentDiffRenderLineBatch = 192;
        private const int AsyncDiffRenderCharThreshold = 24000;
        private readonly Grid _root;
        private readonly Border _headerBorder;
        private readonly TextBlock _pathText;
        private readonly TextBlock _summaryText;
        private readonly StackPanel _diffLinesPanel;
        private readonly ScrollViewer _scrollViewer;
        private readonly Dictionary<string, FrameworkElement> _sectionAnchors = new(StringComparer.Ordinal);
        private string _automationPaneId;
        private string _currentPath;
        private string _currentDiff;
        private IReadOnlyList<GitChangedFile> _currentFiles = Array.Empty<GitChangedFile>();
        private bool _showHeader = true;
        private bool _autoFitWidthLocked;
        private bool _fitWidthRequested;
        private float _lastAppliedZoomFactor = float.NaN;
        private int _maxVisibleLineLength;
        private int _renderGeneration;
        private bool _showLoadingState;
        private bool _disposed;

        public DiffPaneControl()
        {
            _root = new Grid();
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _headerBorder = new Border
            {
                BorderBrush = ResolveBrush("ShellBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 7, 34, 7),
                Background = ResolveBrush("ShellSurfaceBackgroundBrush"),
                Visibility = Visibility.Collapsed,
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

            _diffLinesPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Orientation = Orientation.Vertical,
            };

            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = MinFitWidthZoomFactor,
                MaxZoomFactor = 1.0f,
                Padding = new Thickness(12, 12, 12, 12),
                Content = _diffLinesPanel,
                Background = ResolveBrush("ShellSurfaceBackgroundBrush"),
            };
            _scrollViewer.SizeChanged += (_, _) =>
            {
                UpdateDiffContentWidth();
                QueueZoomToFitWidth();
            };
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

        public bool ShowHeader
        {
            get => _showHeader;
            set
            {
                if (_showHeader == value)
                {
                    return;
                }

                _showHeader = value;
                UpdateHeaderVisibility();
                if (!string.IsNullOrWhiteSpace(_automationPaneId))
                {
                    ApplyAutomationIdentity(_automationPaneId);
                }
            }
        }

        public void ApplyAutomationIdentity(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            _automationPaneId = paneId;
            AutomationProperties.SetAutomationId(this, $"shell-diff-pane-{paneId}");
            AutomationProperties.SetName(this, "Patch review pane");
            string headerPrefix = _showHeader ? "shell-diff-pane" : "shell-diff-pane-inline";
            AutomationProperties.SetAutomationId(_headerBorder, $"{headerPrefix}-header-{paneId}");
            AutomationProperties.SetName(_headerBorder, _showHeader ? "Patch review header" : "Inline patch review header");
            AutomationProperties.SetAutomationId(_pathText, $"{headerPrefix}-path-{paneId}");
            AutomationProperties.SetName(_pathText, _showHeader ? "Patch review path" : "Inline patch review path");
            AutomationProperties.SetAutomationId(_summaryText, $"{headerPrefix}-summary-{paneId}");
            AutomationProperties.SetName(_summaryText, _showHeader ? "Patch review summary" : "Inline patch review summary");
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
            if (_disposed)
            {
                return;
            }

            _scrollViewer.Focus(FocusState.Programmatic);
        }

        public void RequestLayout()
        {
            if (_disposed)
            {
                return;
            }

            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();
            UpdateDiffContentWidth();
            QueueZoomToFitWidth();
        }

        public void DisposePane()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderGeneration++;
            _currentPath = null;
            _currentDiff = null;
            _currentFiles = Array.Empty<GitChangedFile>();
            _showLoadingState = false;
            _sectionAnchors.Clear();
            _diffLinesPanel.Children.Clear();
        }

        public void ApplyFitToWidth(bool autoLock)
        {
            _autoFitWidthLocked = autoLock;
            _fitWidthRequested = true;
            QueueZoomToFitWidth();
        }

        public void SetAutoFitWidth(bool enabled)
        {
            _autoFitWidthLocked = enabled;
            if (enabled)
            {
                _fitWidthRequested = true;
            }
        }

        public void SetDiff(string path, string diffText)
        {
            string normalizedPath = path ?? string.Empty;
            string normalizedDiff = diffText ?? string.Empty;
            if (!_showLoadingState &&
                _currentFiles.Count == 0 &&
                string.Equals(_currentPath ?? string.Empty, normalizedPath, StringComparison.Ordinal) &&
                string.Equals(_currentDiff ?? string.Empty, normalizedDiff, StringComparison.Ordinal))
            {
                _pathText.Text = FormatDiffPathLabel(path);
                _summaryText.Text = string.IsNullOrWhiteSpace(path)
                    ? "Choose a changed file from the inspector."
                    : "Patch view";
                UpdateHeaderVisibility();
                return;
            }

            _currentFiles = Array.Empty<GitChangedFile>();
            _currentPath = normalizedPath;
            _currentDiff = normalizedDiff;
            _showLoadingState = false;

            _pathText.Text = FormatDiffPathLabel(path);
            _summaryText.Text = string.IsNullOrWhiteSpace(path)
                ? "Choose a changed file from the inspector."
                : "Patch view";
            UpdateHeaderVisibility();

            RenderDiff(diffText);
        }

        public void SetLoadingState(string path, string summary = null)
        {
            _currentFiles = Array.Empty<GitChangedFile>();
            _currentPath = path ?? string.Empty;
            _currentDiff = string.Empty;
            _showLoadingState = true;
            _pathText.Text = FormatDiffPathLabel(path);
            _summaryText.Text = string.IsNullOrWhiteSpace(summary)
                ? (string.IsNullOrWhiteSpace(path) ? "Choose a changed file from the inspector." : "Loading patch…")
                : summary;
            UpdateHeaderVisibility();
            RenderDiff(null);
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
            _showLoadingState = false;
            _pathText.Text = string.IsNullOrWhiteSpace(selectedPath)
                ? "All changed files"
                : FormatDiffPathLabel(selectedPath);
            _summaryText.Text = $"{diffFiles.Count} files · Scroll to review the full patch";
            UpdateHeaderVisibility();
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
            Stopwatch stopwatch = Stopwatch.StartNew();
            int renderGeneration = ++_renderGeneration;
            _sectionAnchors.Clear();
            _diffLinesPanel.Children.Clear();
            _maxVisibleLineLength = 0;
            if (string.IsNullOrWhiteSpace(diffText))
            {
                TextBlock emptyText = new()
                {
                    Text = string.IsNullOrWhiteSpace(_currentPath)
                        ? "Select a changed file to inspect its patch."
                        : _showLoadingState
                            ? "Loading patch for the selected file..."
                            : "Patch unavailable for this file.",
                    Foreground = ResolveBrush("ShellTextTertiaryBrush"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                };
                _diffLinesPanel.Children.Add(emptyText);
                _maxVisibleLineLength = Math.Max(_maxVisibleLineLength, emptyText.Text?.Length ?? 0);
                QueueZoomToFitWidth();
                RecordRenderMetrics("single-empty", lineCount: 0, fileCount: 0, durationMs: stopwatch.ElapsedMilliseconds);
                return;
            }

            if (diffText.Length >= AsyncDiffRenderCharThreshold)
            {
                TextBlock loadingIndicator = BuildLoadingIndicator(0, "Preparing patch view…");
                _diffLinesPanel.Children.Add(loadingIndicator);
                _maxVisibleLineLength = Math.Max(_maxVisibleLineLength, loadingIndicator.Text?.Length ?? 0);
                UpdateDiffContentWidth();
                QueueZoomToFitWidth();
                _ = PrepareAndRenderDiffAsync(diffText, renderGeneration, stopwatch);
                RecordRenderMetrics("single-deferred", lineCount: 0, fileCount: 1, durationMs: stopwatch.ElapsedMilliseconds);
                return;
            }

            string[] lines = diffText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            DiffRenderCursor cursor = new()
            {
                Lines = lines,
            };
            RenderDiffLineBatch(cursor, InitialDiffRenderLineBatch);

            if (cursor.NextIndex < lines.Length)
            {
                TextBlock loadingIndicator = BuildLoadingIndicator(lines.Length - cursor.NextIndex);
                _diffLinesPanel.Children.Add(loadingIndicator);
                UpdateDiffContentWidth();
                QueueZoomToFitWidth();
                DispatcherQueue?.TryEnqueue(() => ContinueRenderDiff(cursor, renderGeneration, loadingIndicator, stopwatch));
                RecordRenderMetrics("single-partial", lines.Length, fileCount: 1, durationMs: stopwatch.ElapsedMilliseconds);
                return;
            }

            UpdateDiffContentWidth();
            QueueZoomToFitWidth();
            RecordRenderMetrics("single", lines.Length, fileCount: 1, durationMs: stopwatch.ElapsedMilliseconds);
        }

        private async Task PrepareAndRenderDiffAsync(string diffText, int renderGeneration, Stopwatch stopwatch)
        {
            string[] lines = await Task.Run(() => diffText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)).ConfigureAwait(false);
            if (_disposed || DispatcherQueue is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || renderGeneration != _renderGeneration)
                {
                    return;
                }

                _diffLinesPanel.Children.Clear();
                DiffRenderCursor cursor = new()
                {
                    Lines = lines,
                };
                RenderDiffLineBatch(cursor, InitialDiffRenderLineBatch);

                if (cursor.NextIndex < lines.Length)
                {
                    TextBlock loadingIndicator = BuildLoadingIndicator(lines.Length - cursor.NextIndex);
                    _diffLinesPanel.Children.Add(loadingIndicator);
                    UpdateDiffContentWidth();
                    QueueZoomToFitWidth();
                    DispatcherQueue?.TryEnqueue(() => ContinueRenderDiff(cursor, renderGeneration, loadingIndicator, stopwatch));
                    RecordRenderMetrics("single-partial", lines.Length, fileCount: 1, durationMs: stopwatch.ElapsedMilliseconds);
                    return;
                }

                UpdateDiffContentWidth();
                QueueZoomToFitWidth();
                RecordRenderMetrics("single", lines.Length, fileCount: 1, durationMs: stopwatch.ElapsedMilliseconds);
            });
        }

        private void RenderDiffSet(IReadOnlyList<GitChangedFile> files, string selectedPath)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            _sectionAnchors.Clear();
            _diffLinesPanel.Children.Clear();
            _maxVisibleLineLength = 0;

            if (files is null || files.Count == 0)
            {
                RenderDiff(null);
                return;
            }

            int rowIndex = 0;
            int lineCount = 0;
            foreach (GitChangedFile file in files)
            {
                Border sectionHeader = BuildSectionHeader(file, string.Equals(file.Path, selectedPath, StringComparison.Ordinal));
                _sectionAnchors[file.Path] = sectionHeader;
                AddDiffRow(sectionHeader, rowIndex++);
                _maxVisibleLineLength = Math.Max(_maxVisibleLineLength, file?.DisplayName?.Length ?? 0);

                string[] lines = string.IsNullOrWhiteSpace(file.DiffText)
                    ? new[] { "Patch unavailable for this file." }
                    : file.DiffText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                lineCount += lines.Length;
                List<DiffLineRenderItem> renderItems = BuildRenderItems(lines);
                RenderDiffLineBatch(renderItems, 0, renderItems.Count);
                rowIndex += renderItems.Count;
            }

            UpdateDiffContentWidth();
            QueueZoomToFitWidth();
            ScrollToPath(selectedPath);
            RecordRenderMetrics("full", lineCount, files.Count, stopwatch.ElapsedMilliseconds);
        }

        private List<DiffLineRenderItem> BuildRenderItems(IReadOnlyList<string> lines)
        {
            int oldLineNumber = 0;
            int newLineNumber = 0;
            bool hasActiveHunk = false;
            List<DiffLineRenderItem> items = new(lines.Count);

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

                items.Add(new DiffLineRenderItem
                {
                    OldLineNumber = leftNumber,
                    NewLineNumber = rightNumber,
                    Line = line,
                    Kind = kind,
                });
            }

            return items;
        }

        private void RenderDiffLineBatch(DiffRenderCursor cursor, int count)
        {
            int endIndex = Math.Min(cursor.Lines.Count, cursor.NextIndex + count);
            while (cursor.NextIndex < endIndex)
            {
                string line = cursor.Lines[cursor.NextIndex];
                string kind = ClassifyDiffLine(line);
                int? oldLineNumber = null;
                int? newLineNumber = null;

                if (kind == "hunk")
                {
                    cursor.HasActiveHunk = TryParseHunkHeader(line, out int oldLineStart, out int newLineStart);
                    cursor.OldLineNumber = oldLineStart;
                    cursor.NewLineNumber = newLineStart;
                }
                else if (kind == "deletion")
                {
                    if (cursor.HasActiveHunk)
                    {
                        oldLineNumber = cursor.OldLineNumber++;
                    }
                }
                else if (kind == "addition")
                {
                    if (cursor.HasActiveHunk)
                    {
                        newLineNumber = cursor.NewLineNumber++;
                    }
                }
                else if (kind == "context")
                {
                    if (cursor.HasActiveHunk)
                    {
                        oldLineNumber = cursor.OldLineNumber++;
                        newLineNumber = cursor.NewLineNumber++;
                    }
                }

                _maxVisibleLineLength = Math.Max(_maxVisibleLineLength, line?.Length ?? 0);
                FrameworkElement row = BuildDiffLineRow(oldLineNumber, newLineNumber, line, kind);
                AddDiffRow(row, cursor.NextIndex);
                cursor.NextIndex++;
            }
        }

        private void RenderDiffLineBatch(IReadOnlyList<DiffLineRenderItem> items, int startIndex, int count)
        {
            int endIndex = Math.Min(items.Count, startIndex + count);
            for (int index = startIndex; index < endIndex; index++)
            {
                DiffLineRenderItem item = items[index];
                _maxVisibleLineLength = Math.Max(_maxVisibleLineLength, item.Line?.Length ?? 0);
                FrameworkElement row = BuildDiffLineRow(item.OldLineNumber, item.NewLineNumber, item.Line, item.Kind);
                AddDiffRow(row, index);
            }
        }

        private void ContinueRenderDiff(DiffRenderCursor cursor, int renderGeneration, TextBlock loadingIndicator, Stopwatch stopwatch)
        {
            if (renderGeneration != _renderGeneration)
            {
                return;
            }

            _diffLinesPanel.Children.Remove(loadingIndicator);
            RenderDiffLineBatch(cursor, SubsequentDiffRenderLineBatch);

            if (cursor.NextIndex < cursor.Lines.Count)
            {
                loadingIndicator.Text = $"Rendering patch… {cursor.NextIndex}/{cursor.Lines.Count}";
                _diffLinesPanel.Children.Add(loadingIndicator);
                DispatcherQueue?.TryEnqueue(() => ContinueRenderDiff(cursor, renderGeneration, loadingIndicator, stopwatch));
                return;
            }

            UpdateDiffContentWidth();
            QueueZoomToFitWidth();
            RecordRenderMetrics("single", cursor.Lines.Count, fileCount: 1, durationMs: stopwatch.ElapsedMilliseconds);
        }

        private static TextBlock BuildLoadingIndicator(int remainingLineCount, string text = null)
        {
            return new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(text)
                    ? $"Rendering patch… {remainingLineCount} lines remaining"
                    : text,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A)),
                FontSize = 11,
                Margin = new Thickness(8, 8, 8, 0),
            };
        }

        private void AddDiffRow(FrameworkElement row, int rowIndex)
        {
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
                Text = FormatDiffPathLabel(file.DisplayName),
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

        private static string FormatDiffPathLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "No diff selected";
            }

            string normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
            string fileName = Path.GetFileName(normalized);
            string directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.IsNullOrWhiteSpace(fileName) ? normalized : fileName;
            }

            string compactDirectory = FormatCompactDiffPathContext(directory);
            return string.IsNullOrWhiteSpace(fileName)
                ? compactDirectory
                : $"{fileName} · {compactDirectory}";
        }

        private static string FormatCompactDiffPathContext(string directoryPath, int maxSegments = 2)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return "root";
            }

            string normalized = directoryPath.Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "root";
            }

            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return "root";
            }

            if (segments.Length <= maxSegments)
            {
                return string.Join("/", segments);
            }

            return $".../{string.Join("/", segments[^maxSegments..])}";
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

        private void QueueZoomToFitWidth()
        {
            if ((!_fitWidthRequested && !_autoFitWidthLocked) || DispatcherQueue is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(ApplyZoomToFitWidth);
        }

        private void ApplyZoomToFitWidth()
        {
            if (!_fitWidthRequested && !_autoFitWidthLocked)
            {
                return;
            }

            double availableWidth = _scrollViewer.ActualWidth - _scrollViewer.Padding.Left - _scrollViewer.Padding.Right - 12;
            if (availableWidth <= 1)
            {
                return;
            }

            double estimatedWidth = EstimateDesiredContentWidth();
            float zoomFactor = estimatedWidth <= 0
                ? 1.0f
                : (float)Math.Clamp(availableWidth / estimatedWidth, MinFitWidthZoomFactor, 1.0);
            if (zoomFactor >= 0.98f)
            {
                zoomFactor = 1.0f;
            }

            if (!float.IsNaN(_lastAppliedZoomFactor) && Math.Abs(_lastAppliedZoomFactor - zoomFactor) < 0.01f)
            {
                _fitWidthRequested = false;
                return;
            }

            _lastAppliedZoomFactor = zoomFactor;
            _fitWidthRequested = false;
            _scrollViewer.ChangeView(null, null, zoomFactor, true);
        }

        private double EstimateDesiredContentWidth()
        {
            int maxLineLength = Math.Max(_maxVisibleLineLength, 18);
            return 124d + (maxLineLength * DiffCharacterWidth);
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
            return ShellTheme.ResolveEffectiveTheme(SelfContainedDeployment.SampleConfig.CurrentTheme, SelfContainedDeployment.MainPage.Current?.ActualTheme ?? ElementTheme.Default);
        }

        private static Brush ResolveBrush(string key)
        {
            return ShellTheme.ResolveBrushForTheme(ResolveEffectiveTheme(), key);
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

        private void UpdateHeaderVisibility()
        {
            _headerBorder.Visibility = _showHeader && !string.IsNullOrWhiteSpace(_currentPath) && _currentFiles.Count <= 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static void RecordRenderMetrics(string mode, int lineCount, int fileCount, long durationMs)
        {
            if (lineCount < 200 && durationMs < 60 && fileCount <= 1)
            {
                return;
            }

            NativeAutomationEventLog.Record("render", "diff.rendered", $"Rendered {mode} diff", new Dictionary<string, string>
            {
                ["mode"] = mode ?? "single",
                ["lineCount"] = lineCount.ToString(),
                ["fileCount"] = fileCount.ToString(),
                ["durationMs"] = durationMs.ToString(),
            });
        }
    }
}
