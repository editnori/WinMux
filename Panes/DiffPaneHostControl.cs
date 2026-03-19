using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Git;
using System;
using System.Collections.Generic;
using System.IO;

namespace SelfContainedDeployment.Panes
{
    public enum DiffPaneDisplayMode
    {
        FileCompare,
        FullPatchReview,
    }

    public sealed class DiffPaneHostControl : UserControl
    {
        private readonly Grid _root;
        private readonly Border _fileHeader;
        private readonly TextBlock _fileHeaderText;
        private readonly Button _previousChangeButton;
        private readonly Button _nextChangeButton;
        private readonly EditorPaneControl _comparePane;
        private readonly DiffPaneControl _patchPane;
        private DiffPaneDisplayMode _displayMode = DiffPaneDisplayMode.FileCompare;
        private string _lastInlineDiffPath = string.Empty;
        private string _lastInlineDiffText = string.Empty;
        private bool _lastInlineDiffLoading;
        private IReadOnlyList<GitChangedFile> _lastFullPatchFiles = Array.Empty<GitChangedFile>();
        private string _lastFullPatchSelectedPath = string.Empty;

        public DiffPaneHostControl()
        {
            _comparePane = new EditorPaneControl
            {
                DiffModeEnabled = true,
                ShowCompactHeader = false,
            };
            _patchPane = new DiffPaneControl
            {
                ShowHeader = false,
                Visibility = Visibility.Collapsed,
            };

            _comparePane.InteractionRequested += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);
            _patchPane.InteractionRequested += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);

            _root = new Grid();
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _fileHeaderText = new TextBlock
            {
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Grid headerLayout = new()
            {
                ColumnSpacing = 8,
            };
            headerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerLayout.Children.Add(_fileHeaderText);

            _previousChangeButton = BuildNavigationButton("Prev", (_, _) => _patchPane.NavigateToPreviousChange(), "Previous diff section");
            Grid.SetColumn(_previousChangeButton, 1);
            headerLayout.Children.Add(_previousChangeButton);

            _nextChangeButton = BuildNavigationButton("Next", (_, _) => _patchPane.NavigateToNextChange(), "Next diff section");
            Grid.SetColumn(_nextChangeButton, 2);
            headerLayout.Children.Add(_nextChangeButton);

            _fileHeader = new Border
            {
                Padding = new Thickness(5, 3, 6, 3),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Visibility = Visibility.Collapsed,
                Child = headerLayout,
            };

            Grid.SetRow(_patchPane, 1);
            Grid.SetRow(_comparePane, 1);
            _root.Children.Add(_fileHeader);
            _root.Children.Add(_patchPane);
            _root.Children.Add(_comparePane);
            Content = _root;
        }

        public event EventHandler InteractionRequested;

        public string ProjectRootPath
        {
            get => _comparePane.ProjectRootPath;
            set => _comparePane.ProjectRootPath = value;
        }

        public DiffPaneDisplayMode DisplayMode => _displayMode;

        public bool RequiresCompleteSnapshot => _displayMode == DiffPaneDisplayMode.FullPatchReview;

        public void ApplyAutomationIdentity(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            AutomationProperties.SetAutomationId(this, $"shell-diff-pane-host-{paneId}");
            AutomationProperties.SetName(this, "Patch review");
            AutomationProperties.SetAutomationId(_fileHeader, $"shell-diff-pane-header-{paneId}");
            AutomationProperties.SetName(_fileHeader, "Patch review file header");
            AutomationProperties.SetAutomationId(_previousChangeButton, $"shell-diff-pane-prev-change-{paneId}");
            AutomationProperties.SetName(_previousChangeButton, "Previous diff section");
            AutomationProperties.SetAutomationId(_nextChangeButton, $"shell-diff-pane-next-change-{paneId}");
            AutomationProperties.SetName(_nextChangeButton, "Next diff section");
            _comparePane.ApplyAutomationIdentity(paneId, "shell-diff-pane", "Patch review");
            _patchPane.ApplyAutomationIdentity(paneId);
        }

        public void ApplyTheme(ElementTheme theme)
        {
            RequestedTheme = theme;
            _fileHeader.Background = ResolveBrush("ShellSurfaceBackgroundBrush");
            _fileHeader.BorderBrush = ResolveBrush("ShellBorderBrush");
            _fileHeaderText.Foreground = ResolveBrush("ShellTextSecondaryBrush");
            _comparePane.ApplyTheme(theme);
            _patchPane.ApplyTheme(theme);
        }

        public void FocusPane()
        {
            if (_patchPane.Visibility == Visibility.Visible)
            {
                _patchPane.FocusPane();
            }
            else
            {
                _comparePane.FocusPane();
            }
        }

        public void RequestLayout()
        {
            if (_patchPane.Visibility == Visibility.Visible)
            {
                _patchPane.RequestLayout();
            }
            else
            {
                _comparePane.RequestLayout();
            }
        }

        public void SetLiveResizeMode(bool enabled)
        {
            _comparePane.SetLiveResizeMode(enabled);
            if (!enabled)
            {
                RequestLayout();
            }
        }

        public void DisposePane()
        {
            _comparePane.DisposePane();
            _patchPane.DisposePane();
        }

        public void ApplyFitToWidth(bool autoLock)
        {
            _comparePane.ApplyFitToWidth(autoLock);
            _patchPane.ApplyFitToWidth(autoLock);
        }

        public void SetAutoFitWidth(bool enabled)
        {
            _comparePane.SetAutoFitWidth(enabled);
            _patchPane.SetAutoFitWidth(enabled);
        }

        internal DiffPaneRenderSnapshot GetRenderSnapshot(int maxLines = 0)
        {
            return _patchPane.Visibility == Visibility.Visible
                ? _patchPane.GetRenderSnapshot(maxLines)
                : _comparePane.GetDiffRenderSnapshot(maxLines);
        }

        internal void ShowFileCompare(GitChangedFile changedFile, string summary = null)
        {
            bool sameMode = _displayMode == DiffPaneDisplayMode.FileCompare;
            string diffPath = changedFile?.Path ?? string.Empty;
            string diffText = changedFile?.DiffText ?? string.Empty;
            _displayMode = DiffPaneDisplayMode.FileCompare;
            SetFileHeader(changedFile?.Path);
            _comparePane.Visibility = Visibility.Collapsed;
            _patchPane.Visibility = Visibility.Visible;
            if (sameMode &&
                !_lastInlineDiffLoading &&
                string.Equals(_lastInlineDiffPath, diffPath, StringComparison.Ordinal) &&
                string.Equals(_lastInlineDiffText, diffText, StringComparison.Ordinal))
            {
                return;
            }

            _lastInlineDiffPath = diffPath;
            _lastInlineDiffText = diffText;
            _lastInlineDiffLoading = false;
            _lastFullPatchFiles = Array.Empty<GitChangedFile>();
            _lastFullPatchSelectedPath = string.Empty;
            _patchPane.SetDiff(changedFile?.Path, changedFile?.DiffText);
            UpdateChangeNavigationState();
        }

        internal void ShowUnifiedDiff(string path, string diffText)
        {
            bool sameMode = _displayMode == DiffPaneDisplayMode.FileCompare;
            string normalizedPath = path ?? string.Empty;
            string normalizedDiffText = diffText ?? string.Empty;
            _displayMode = DiffPaneDisplayMode.FileCompare;
            SetFileHeader(path);
            _comparePane.Visibility = Visibility.Collapsed;
            _patchPane.Visibility = Visibility.Visible;
            if (sameMode &&
                !_lastInlineDiffLoading &&
                string.Equals(_lastInlineDiffPath, normalizedPath, StringComparison.Ordinal) &&
                string.Equals(_lastInlineDiffText, normalizedDiffText, StringComparison.Ordinal))
            {
                return;
            }

            _lastInlineDiffPath = normalizedPath;
            _lastInlineDiffText = normalizedDiffText;
            _lastInlineDiffLoading = false;
            _lastFullPatchFiles = Array.Empty<GitChangedFile>();
            _lastFullPatchSelectedPath = string.Empty;
            _patchPane.SetDiff(path, diffText);
            UpdateChangeNavigationState();
        }

        internal void ShowLoading(string path, string summary = null)
        {
            bool sameMode = _displayMode == DiffPaneDisplayMode.FileCompare;
            string normalizedPath = path ?? string.Empty;
            _displayMode = DiffPaneDisplayMode.FileCompare;
            SetFileHeader(path);
            _comparePane.Visibility = Visibility.Collapsed;
            _patchPane.Visibility = Visibility.Visible;
            if (sameMode &&
                string.Equals(_lastInlineDiffPath, normalizedPath, StringComparison.Ordinal) &&
                _lastInlineDiffLoading &&
                string.IsNullOrEmpty(_lastInlineDiffText))
            {
                return;
            }

            _lastInlineDiffPath = normalizedPath;
            _lastInlineDiffText = string.Empty;
            _lastInlineDiffLoading = true;
            _lastFullPatchFiles = Array.Empty<GitChangedFile>();
            _lastFullPatchSelectedPath = string.Empty;
            _patchPane.SetLoadingState(path, summary);
            UpdateChangeNavigationState();
        }

        internal void ShowFullPatch(IReadOnlyList<GitChangedFile> files, string selectedPath)
        {
            bool sameMode = _displayMode == DiffPaneDisplayMode.FullPatchReview;
            string normalizedSelectedPath = selectedPath ?? string.Empty;
            _displayMode = DiffPaneDisplayMode.FullPatchReview;
            SetFileHeader(null);
            _comparePane.Visibility = Visibility.Collapsed;
            _patchPane.Visibility = Visibility.Visible;
            if (sameMode &&
                ReferenceEquals(_lastFullPatchFiles, files) &&
                string.Equals(_lastFullPatchSelectedPath, normalizedSelectedPath, StringComparison.Ordinal))
            {
                return;
            }

            _lastInlineDiffPath = string.Empty;
            _lastInlineDiffText = string.Empty;
            _lastInlineDiffLoading = false;
            _lastFullPatchFiles = files ?? Array.Empty<GitChangedFile>();
            _lastFullPatchSelectedPath = normalizedSelectedPath;
            _patchPane.SetDiffSet(files, selectedPath);
            UpdateChangeNavigationState();
        }

        private void SetFileHeader(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _fileHeader.Visibility = _displayMode == DiffPaneDisplayMode.FullPatchReview ? Visibility.Visible : Visibility.Collapsed;
                _fileHeaderText.Text = _displayMode == DiffPaneDisplayMode.FullPatchReview ? "All changed files" : string.Empty;
                UpdateChangeNavigationState();
                return;
            }

            string normalized = path.Replace('\\', '/').TrimEnd('/');
            string fileName = Path.GetFileName(normalized);
            _fileHeaderText.Text = string.IsNullOrWhiteSpace(fileName) ? normalized : fileName;
            _fileHeader.Visibility = Visibility.Visible;
            UpdateChangeNavigationState();
        }

        private Button BuildNavigationButton(string label, RoutedEventHandler handler, string toolTip)
        {
            Button button = new()
            {
                Content = new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                Style = Application.Current.Resources["ShellDiffNavigationButtonStyle"] as Style,
                Visibility = Visibility.Collapsed,
                IsEnabled = false,
            };
            ToolTipService.SetToolTip(button, toolTip);
            button.Click += handler;
            return button;
        }

        private void UpdateChangeNavigationState()
        {
            bool showNavigation = _fileHeader.Visibility == Visibility.Visible && _patchPane.Visibility == Visibility.Visible;
            bool canNavigate = showNavigation && _patchPane.CanNavigateChanges;
            if (_previousChangeButton is not null)
            {
                _previousChangeButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                _previousChangeButton.IsEnabled = canNavigate;
            }

            if (_nextChangeButton is not null)
            {
                _nextChangeButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                _nextChangeButton.IsEnabled = canNavigate;
            }
        }

        private Brush ResolveBrush(string key)
        {
            ElementTheme theme = RequestedTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
            return ShellTheme.ResolveBrushForTheme(theme, key, Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A));
        }
    }
}
