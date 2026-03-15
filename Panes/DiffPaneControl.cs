using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;

namespace SelfContainedDeployment.Panes
{
    public sealed class DiffPaneControl : UserControl
    {
        private readonly TextBlock _pathText;
        private readonly TextBlock _summaryText;
        private readonly RichTextBlock _diffBlock;
        private readonly ScrollViewer _scrollViewer;
        private string _currentPath;
        private string _currentDiff;

        public DiffPaneControl()
        {
            Grid root = new();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border headerBorder = new()
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

            headerBorder.Child = headerStack;
            root.Children.Add(headerBorder);

            _diffBlock = new RichTextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap,
            };

            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12, 12, 12, 12),
                Content = _diffBlock,
                Background = ResolveBrush("ShellMutedSurfaceBrush"),
            };
            Grid.SetRow(_scrollViewer, 1);
            root.Children.Add(_scrollViewer);

            Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            Content = root;

            PointerPressed += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);
            GotFocus += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler InteractionRequested;

        public string CurrentPath => _currentPath;

        public string CurrentDiff => _currentDiff;

        public void ApplyTheme(ElementTheme theme)
        {
            RequestedTheme = theme;
            Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            _scrollViewer.Background = ResolveBrush("ShellMutedSurfaceBrush");
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
        }

        public void SetDiff(string path, string diffText)
        {
            _currentPath = path ?? string.Empty;
            _currentDiff = diffText ?? string.Empty;

            _pathText.Text = string.IsNullOrWhiteSpace(path) ? "No diff selected" : path.Replace('\\', '/');
            _summaryText.Text = string.IsNullOrWhiteSpace(path)
                ? "Choose a changed file from the inspector."
                : string.IsNullOrWhiteSpace(diffText)
                    ? "Loading patch…"
                    : "Patch view";

            RenderDiff(diffText);
        }

        private void RenderDiff(string diffText)
        {
            _diffBlock.Blocks.Clear();
            if (string.IsNullOrWhiteSpace(diffText))
            {
                Paragraph emptyParagraph = new();
                emptyParagraph.Inlines.Add(new Run
                {
                    Text = string.IsNullOrWhiteSpace(_currentPath)
                        ? "Select a changed file to inspect its patch."
                        : "Loading patch…",
                    Foreground = ResolveBrush("ShellTextTertiaryBrush"),
                });
                _diffBlock.Blocks.Add(emptyParagraph);
                return;
            }

            Paragraph paragraph = new()
            {
                Margin = new Thickness(0),
            };

            string[] lines = diffText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                paragraph.Inlines.Add(new Run
                {
                    Text = line,
                    Foreground = ResolveBrush(ResolveDiffLineBrushKey(line)),
                });

                if (index < lines.Length - 1)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }
            }

            _diffBlock.Blocks.Add(paragraph);
        }

        private static string ResolveDiffLineBrushKey(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return "ShellTextSecondaryBrush";
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                return "ShellSuccessBrush";
            }

            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                return "ShellDangerBrush";
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                return "ShellInfoBrush";
            }

            if (line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("+++", StringComparison.Ordinal))
            {
                return "ShellTextSecondaryBrush";
            }

            return "ShellTextPrimaryBrush";
        }

        private static Brush ResolveBrush(string key)
        {
            return (Brush)Application.Current.Resources[key];
        }
    }
}
