using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.Foundation;

namespace SelfContainedDeployment
{
    public partial class MainPage
    {
        private static string ResolvePaneAccentBrushKey(WorkspacePaneKind kind)
        {
            return kind switch
            {
                WorkspacePaneKind.Browser => "ShellInfoBrush",
                WorkspacePaneKind.Editor => "ShellSuccessBrush",
                WorkspacePaneKind.Diff => "ShellWarningBrush",
                _ => "ShellTerminalBrush",
            };
        }

        private void RemovePaneContainer(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId) || !_paneContainersById.TryGetValue(paneId, out Border container))
            {
                return;
            }

            PaneWorkspaceGrid.Children.Remove(container);
            container.Child = null;
            _paneContainersById.Remove(paneId);
        }

        private void RenderPaneWorkspace()
        {
            string renderKey = BuildPaneWorkspaceRenderKey();
            int visiblePaneCount = _activeThread is null || _showingSettings
                ? 0
                : GetVisiblePanes(_activeThread).Count();
            bool cacheHit = string.Equals(renderKey, _lastPaneWorkspaceRenderKey, StringComparison.Ordinal);
            var perfData = new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
                ["selectedPaneId"] = _activeThread?.SelectedPaneId ?? string.Empty,
                ["zoomedPaneId"] = _activeThread?.ZoomedPaneId ?? string.Empty,
                ["visiblePaneCount"] = visiblePaneCount.ToString(),
                ["showingSettings"] = _showingSettings.ToString(),
                ["cacheHit"] = cacheHit.ToString(),
                ["reason"] = cacheHit ? "selection-chrome" : visiblePaneCount == 0 ? "empty-or-hidden" : "layout-rebuild",
                ["renderKey"] = renderKey,
            };
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("render.pane-workspace", data: perfData);
            NativeAutomationDiagnostics.IncrementCounter("paneWorkspaceRenderCount");
            if (cacheHit)
            {
                UpdatePaneSelectionChrome();
                return;
            }

            _lastPaneWorkspaceRenderKey = renderKey;
            RemovePaneSplitters();
            PaneWorkspaceGrid.RowDefinitions.Clear();
            PaneWorkspaceGrid.ColumnDefinitions.Clear();

            if (_activeThread is null || _showingSettings)
            {
                foreach (Border container in _paneContainersById.Values)
                {
                    container.Visibility = Visibility.Collapsed;
                }

                return;
            }

            List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(_activeThread).ToList();
            if (visiblePanes.Count == 0)
            {
                foreach (Border container in _paneContainersById.Values)
                {
                    container.Visibility = Visibility.Collapsed;
                }

                return;
            }

            foreach (Border container in _paneContainersById.Values)
            {
                container.Visibility = Visibility.Collapsed;
            }

            switch (visiblePanes.Count)
            {
                case 1:
                    PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    AddPaneCell(visiblePanes[0], 0, 0);
                    break;
                case 2:
                    PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0);
                    AddVerticalSplitter(0, 1);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    break;
                case 3:
                    ConfigureSplitRows(_activeThread.SecondarySplitRatio);
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0, rowSpan: 3);
                    AddVerticalSplitter(0, 1, rowSpan: 3);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    AddHorizontalSplitter(1, 2);
                    AddCenterSplitter(1, 1);
                    AddPaneCell(visiblePanes[2], 2, 2);
                    break;
                default:
                    ConfigureSplitRows(_activeThread.SecondarySplitRatio);
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0);
                    AddVerticalSplitter(0, 1, rowSpan: 3);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    AddHorizontalSplitter(1, 0, columnSpan: 3);
                    AddCenterSplitter(1, 1);
                    AddPaneCell(visiblePanes[2], 2, 0);
                    AddPaneCell(visiblePanes[3], 2, 2);
                    break;
            }

            SyncAutoFitStateForVisiblePanes(_activeThread);
            UpdatePaneSelectionChrome();
        }

        private void AddPaneCell(WorkspacePaneRecord pane, int row, int column, int rowSpan = 1, int columnSpan = 1)
        {
            if (!_paneContainersById.TryGetValue(pane.Id, out Border border))
            {
                Grid containerContent = BuildPaneContainerContent(pane);
                border = new Border
                {
                    BorderThickness = new Thickness(1, 0, 1, 1),
                    CornerRadius = new CornerRadius(5),
                    Child = containerContent,
                    Margin = new Thickness(1, 0, 1, 1),
                    Tag = pane,
                };
                AutomationProperties.SetAutomationId(border, $"shell-pane-{pane.Id}");
                border.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPaneContainerPointerPressed), true);
                _paneContainersById[pane.Id] = border;
            }
            else
            {
                if (!ReferenceEquals(border.Tag, pane))
                {
                    border.Child = BuildPaneContainerContent(pane);
                }

                border.Tag = pane;
            }

            border.Background = AppBrush(border, "ShellSurfaceBackgroundBrush");
            border.Visibility = Visibility.Visible;
            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            Grid.SetRowSpan(border, rowSpan);
            Grid.SetColumnSpan(border, columnSpan);
            if (!ReferenceEquals(border.Parent, PaneWorkspaceGrid))
            {
                PaneWorkspaceGrid.Children.Add(border);
            }

            UpdatePaneZoomButtonState(border, pane);
        }

        private void RemovePaneSplitters()
        {
            foreach (Border splitter in PaneWorkspaceGrid.Children
                         .OfType<Border>()
                         .Where(candidate => candidate.Tag is string direction &&
                             (string.Equals(direction, "vertical", StringComparison.Ordinal) ||
                              string.Equals(direction, "horizontal", StringComparison.Ordinal) ||
                              string.Equals(direction, "both", StringComparison.Ordinal)))
                         .ToList())
            {
                PaneWorkspaceGrid.Children.Remove(splitter);
            }
        }

        private Grid BuildPaneContainerContent(WorkspacePaneRecord pane)
        {
            Grid content = new();
            content.Children.Add(pane.View);

            Button zoomButton = new()
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = ResolvePaneZoomButtonMargin(pane),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)Application.Current.Resources["ShellGhostToolbarButtonStyle"],
                Tag = pane,
                Opacity = 0.84,
            };
            zoomButton.Click += OnPaneZoomButtonClicked;
            content.Children.Add(zoomButton);
            return content;
        }

        private static Thickness ResolvePaneZoomButtonMargin(WorkspacePaneRecord pane)
        {
            return new Thickness(0, 0, 1, 0);
        }

        private void UpdatePaneZoomButtonState(Border border, WorkspacePaneRecord pane)
        {
            if (border?.Child is not Grid content)
            {
                return;
            }

            Button zoomButton = content.Children.OfType<Button>().FirstOrDefault(button => ReferenceEquals(button.Tag, pane) || button.Tag is WorkspacePaneRecord);
            if (zoomButton is null)
            {
                return;
            }

            zoomButton.Tag = pane;
            zoomButton.Margin = ResolvePaneZoomButtonMargin(pane);
            bool isZoomed = string.Equals(_activeThread?.ZoomedPaneId, pane.Id, StringComparison.Ordinal);
            bool isSelected = string.Equals(_activeThread?.SelectedPaneId, pane.Id, StringComparison.Ordinal);
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            Brush selectionBrush = AppBrush(border, ResolvePaneAccentBrushKey(pane.Kind));
            zoomButton.Content = new FontIcon
            {
                FontSize = 9.25,
                Glyph = isZoomed ? "\uE73F" : "\uE740",
            };
            zoomButton.Opacity = isZoomed ? 1.0 : 0.78;
            zoomButton.Background = isZoomed
                ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x10 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : isSelected
                    ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x08 : (byte)0x0C, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            zoomButton.BorderBrush = isZoomed
                ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x50 : (byte)0x46, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : isSelected
                    ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x24 : (byte)0x2E, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            zoomButton.Foreground = isZoomed
                ? AppBrush(border, "ShellTextPrimaryBrush")
                : isSelected
                    ? selectionBrush
                    : AppBrush(border, "ShellTextTertiaryBrush");
            AutomationProperties.SetAutomationId(zoomButton, $"shell-pane-zoom-{pane.Id}");
            AutomationProperties.SetName(zoomButton, isZoomed ? "Restore pane layout" : "Focus pane");
            ToolTipService.SetToolTip(zoomButton, isZoomed ? "Restore pane layout" : "Focus this pane");
        }

        private void ConfigureSplitColumns(double primaryRatio)
        {
            double clampedRatio = ClampPaneSplitRatio(primaryRatio);
            PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clampedRatio, GridUnitType.Star) });
            PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PaneDividerThickness) });
            PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - clampedRatio, GridUnitType.Star) });
        }

        private void ConfigureSplitRows(double primaryRatio)
        {
            double clampedRatio = ClampPaneSplitRatio(primaryRatio);
            PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(clampedRatio, GridUnitType.Star) });
            PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PaneDividerThickness) });
            PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - clampedRatio, GridUnitType.Star) });
        }

        private void AddVerticalSplitter(int row, int column, int rowSpan = 1)
        {
            Border splitter = new()
            {
                Width = PaneDividerThickness,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Tag = "vertical",
            };
            ApplyPaneSplitterVisual(splitter, emphasized: false);
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-vertical-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize. Hold Shift to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.PointerCaptureLost += OnPaneSplitterPointerCaptureLost;
            splitter.PointerEntered += OnPaneSplitterPointerEntered;
            splitter.PointerExited += OnPaneSplitterPointerExited;
            splitter.DoubleTapped += OnPaneSplitterDoubleTapped;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Grid.SetRowSpan(splitter, rowSpan);
            PaneWorkspaceGrid.Children.Add(splitter);
        }

        private void AddHorizontalSplitter(int row, int column, int columnSpan = 1)
        {
            Border splitter = new()
            {
                Height = PaneDividerThickness,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Tag = "horizontal",
            };
            ApplyPaneSplitterVisual(splitter, emphasized: false);
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-horizontal-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize. Hold Shift to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.PointerCaptureLost += OnPaneSplitterPointerCaptureLost;
            splitter.PointerEntered += OnPaneSplitterPointerEntered;
            splitter.PointerExited += OnPaneSplitterPointerExited;
            splitter.DoubleTapped += OnPaneSplitterDoubleTapped;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Grid.SetColumnSpan(splitter, columnSpan);
            PaneWorkspaceGrid.Children.Add(splitter);
        }

        private void AddCenterSplitter(int row, int column)
        {
            Border splitter = new()
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(-4),
                Tag = "both",
            };
            ApplyPaneSplitterVisual(splitter, emphasized: false);
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-both-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.PointerCaptureLost += OnPaneSplitterPointerCaptureLost;
            splitter.PointerEntered += OnPaneSplitterPointerEntered;
            splitter.PointerExited += OnPaneSplitterPointerExited;
            splitter.DoubleTapped += OnPaneSplitterDoubleTapped;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Canvas.SetZIndex(splitter, 3);
            PaneWorkspaceGrid.Children.Add(splitter);
        }

        private void OnPaneSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeThread is null || sender is not Border splitter || splitter.Tag is not string direction)
            {
                return;
            }

            _activeSplitter = splitter;
            _activeSplitterDirection = direction;
            _activeSplitterPointerId = e.Pointer.PointerId;
            Point point = e.GetCurrentPoint(PaneWorkspaceGrid).Position;
            _splitterDragOriginX = point.X;
            _splitterDragOriginY = point.Y;
            _splitterStartPrimaryRatio = _activeThread.PrimarySplitRatio;
            _splitterStartSecondaryRatio = _activeThread.SecondarySplitRatio;
            _splitterPreviewPrimaryRatio = _splitterStartPrimaryRatio;
            _splitterPreviewSecondaryRatio = _splitterStartSecondaryRatio;
            ResetPaneSplitPreview();
            ShowPaneSplitPreview();
            UpdatePaneSplitPreviewVisuals();
            ApplyPaneSplitterVisual(splitter, emphasized: true);
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPaneSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_activeThread is null || sender is not Border splitter || !ReferenceEquals(splitter, _activeSplitter) || _activeSplitterPointerId != e.Pointer.PointerId)
            {
                return;
            }

            Point point = e.GetCurrentPoint(PaneWorkspaceGrid).Position;
            bool resizeBothAxes = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == Windows.System.VirtualKeyModifiers.Shift;

            if (string.Equals(_activeSplitterDirection, "vertical", StringComparison.Ordinal))
            {
                UpdatePaneSplitPreviewFromPointer(point, adjustPrimary: true, adjustSecondary: resizeBothAxes);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "both", StringComparison.Ordinal))
            {
                UpdatePaneSplitPreviewFromPointer(point, adjustPrimary: true, adjustSecondary: true);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "horizontal", StringComparison.Ordinal))
            {
                UpdatePaneSplitPreviewFromPointer(point, adjustPrimary: resizeBothAxes, adjustSecondary: true);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            EqualizeVisiblePaneSplits(_activeThread, equalizePrimary: true, equalizeSecondary: true, reason: "splitter");
            e.Handled = true;
        }

        private void OnPaneSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && ReferenceEquals(splitter, _activeSplitter) && _activeSplitterPointerId == e.Pointer.PointerId)
            {
                CompletePaneSplitterInteraction(splitter, commitPreview: true);
                splitter.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && ReferenceEquals(splitter, _activeSplitter) && _activeSplitterPointerId == e.Pointer.PointerId)
            {
                CompletePaneSplitterInteraction(splitter, commitPreview: false);
                splitter.ReleasePointerCaptures();
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter &&
                ReferenceEquals(splitter, _activeSplitter) &&
                (!_activeSplitterPointerId.HasValue || _activeSplitterPointerId == e.Pointer.PointerId))
            {
                CompletePaneSplitterInteraction(splitter, commitPreview: false);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && !ReferenceEquals(splitter, _activeSplitter))
            {
                ApplyPaneSplitterVisual(splitter, emphasized: true);
            }
        }

        private void OnPaneSplitterPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && !ReferenceEquals(splitter, _activeSplitter))
            {
                ApplyPaneSplitterVisual(splitter, emphasized: false);
            }
        }

        private void ApplyPaneSplitterVisual(Border splitter, bool emphasized)
        {
            if (splitter is null || PaneWorkspaceGrid is null)
            {
                return;
            }

            bool isCenterSplitter = string.Equals(splitter.Tag as string, "both", StringComparison.Ordinal);
            if (isCenterSplitter)
            {
                splitter.Background = emphasized
                    ? CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellPaneActiveBorderBrush"), 0x10, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                    : AppBrush(PaneWorkspaceGrid, "ShellSurfaceBackgroundBrush");
                splitter.BorderBrush = emphasized
                    ? CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellPaneActiveBorderBrush"), 0x40, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                    : CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellBorderBrush"), 0x30, Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
                return;
            }

            splitter.Background = emphasized
                ? CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellPaneActiveBorderBrush"), 0x20, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                : CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellBorderBrush"), 0x20, Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
        }

        private void ClearActiveSplitterTracking()
        {
            _activeSplitter = null;
            _activeSplitterDirection = null;
            _activeSplitterPointerId = null;
            _splitterPreviewPrimaryRatio = null;
            _splitterPreviewSecondaryRatio = null;
        }

        private void UpdatePaneSplitPreviewFromPointer(Point point, bool adjustPrimary, bool adjustSecondary)
        {
            if (_activeThread is null)
            {
                return;
            }

            double horizontalOffset = 0;
            double verticalOffset = 0;

            if (adjustPrimary && PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                double leftWidth = PaneWorkspaceGrid.ColumnDefinitions[0].ActualWidth;
                double rightWidth = PaneWorkspaceGrid.ColumnDefinitions[2].ActualWidth;
                double totalWidth = leftWidth + rightWidth;
                if (totalWidth > 0)
                {
                    double nextLeftWidth = Math.Clamp((totalWidth * _splitterStartPrimaryRatio) + (point.X - _splitterDragOriginX), totalWidth * MinPaneSplitRatio, totalWidth * MaxPaneSplitRatio);
                    _splitterPreviewPrimaryRatio = ClampPaneSplitRatio(nextLeftWidth / totalWidth);
                    horizontalOffset = nextLeftWidth - (totalWidth * _splitterStartPrimaryRatio);
                }
            }

            if (adjustSecondary && PaneWorkspaceGrid.RowDefinitions.Count >= 3)
            {
                double topHeight = PaneWorkspaceGrid.RowDefinitions[0].ActualHeight;
                double bottomHeight = PaneWorkspaceGrid.RowDefinitions[2].ActualHeight;
                double totalHeight = topHeight + bottomHeight;
                if (totalHeight > 0)
                {
                    double nextTopHeight = Math.Clamp((totalHeight * _splitterStartSecondaryRatio) + (point.Y - _splitterDragOriginY), totalHeight * MinPaneSplitRatio, totalHeight * MaxPaneSplitRatio);
                    _splitterPreviewSecondaryRatio = ClampPaneSplitRatio(nextTopHeight / totalHeight);
                    verticalOffset = nextTopHeight - (totalHeight * _splitterStartSecondaryRatio);
                }
            }

            ApplyPaneSplitPreview(horizontalOffset, verticalOffset);
            UpdatePaneSplitPreviewVisuals();
        }

        private void ApplyPaneSplitPreview(double horizontalOffset, double verticalOffset)
        {
            foreach (Border splitter in PaneWorkspaceGrid.Children.OfType<Border>())
            {
                string direction = splitter.Tag as string;
                switch (direction)
                {
                    case "vertical":
                        SetPaneSplitPreviewTransform(splitter, horizontalOffset, 0);
                        break;
                    case "horizontal":
                        SetPaneSplitPreviewTransform(splitter, 0, verticalOffset);
                        break;
                    case "both":
                        SetPaneSplitPreviewTransform(splitter, horizontalOffset, verticalOffset);
                        break;
                }
            }
        }

        private void ShowPaneSplitPreview()
        {
            if (PaneSplitPreviewCanvas is null)
            {
                return;
            }

            PaneSplitPreviewCanvas.Children.Clear();
            _paneSplitPreviewItems.Clear();
            PaneSplitPreviewCanvas.Visibility = Visibility.Visible;
        }

        private void HidePaneSplitPreview()
        {
            if (PaneSplitPreviewCanvas is null)
            {
                return;
            }

            PaneSplitPreviewCanvas.Children.Clear();
            _paneSplitPreviewItems.Clear();
            PaneSplitPreviewCanvas.Visibility = Visibility.Collapsed;
        }

        private void UpdatePaneSplitPreviewVisuals()
        {
            if (PaneSplitPreviewCanvas is null || _activeThread is null || PaneSplitPreviewCanvas.Visibility != Visibility.Visible)
            {
                return;
            }

            List<(WorkspacePaneRecord Pane, Rect Bounds)> previewRects = BuildPaneSplitPreviewRects();
            EnsurePaneSplitPreviewItems(previewRects.Count);

            for (int index = 0; index < _paneSplitPreviewItems.Count; index++)
            {
                Border previewBorder = _paneSplitPreviewItems[index];
                if (index >= previewRects.Count)
                {
                    previewBorder.Visibility = Visibility.Collapsed;
                    continue;
                }

                (WorkspacePaneRecord pane, Rect bounds) = previewRects[index];
                Rect insetBounds = InsetPreviewRect(bounds, 6);
                previewBorder.Width = Math.Max(0, insetBounds.Width);
                previewBorder.Height = Math.Max(0, insetBounds.Height);
                Canvas.SetLeft(previewBorder, insetBounds.X);
                Canvas.SetTop(previewBorder, insetBounds.Y);
                previewBorder.Visibility = Visibility.Visible;

                Brush accentBrush = AppBrush(PaneWorkspaceGrid, ResolvePaneAccentBrushKey(pane.Kind));
                previewBorder.BorderBrush = CreateSidebarTintedBrush(accentBrush, 0x7C, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                previewBorder.Background = CreateSidebarTintedBrush(accentBrush, 0x14, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                if (previewBorder.Child is Grid previewContent)
                {
                    previewContent.Background = null;
                    if (previewContent.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock label)
                    {
                        label.Text = BuildOverviewPaneLabel(pane);
                        label.Foreground = accentBrush;
                    }
                }
            }
        }

        private void EnsurePaneSplitPreviewItems(int count)
        {
            if (PaneSplitPreviewCanvas is null)
            {
                return;
            }

            while (_paneSplitPreviewItems.Count < count)
            {
                TextBlock label = new()
                {
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 6, 8, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };

                Grid previewContent = new();
                previewContent.Children.Add(label);

                Border previewBorder = new()
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = 1,
                    Child = previewContent,
                };

                previewBorder.Visibility = Visibility.Collapsed;
                _paneSplitPreviewItems.Add(previewBorder);
                PaneSplitPreviewCanvas.Children.Add(previewBorder);
            }
        }

        private List<(WorkspacePaneRecord Pane, Rect Bounds)> BuildPaneSplitPreviewRects()
        {
            List<(WorkspacePaneRecord Pane, Rect Bounds)> result = new();
            if (_activeThread is null || PaneWorkspaceGrid is null)
            {
                return result;
            }

            List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(_activeThread).ToList();
            if (visiblePanes.Count == 0)
            {
                return result;
            }

            double totalWidth = Math.Max(0, PaneWorkspaceGrid.ActualWidth);
            double totalHeight = Math.Max(0, PaneWorkspaceGrid.ActualHeight);
            if (totalWidth <= 1 || totalHeight <= 1)
            {
                return result;
            }

            double primaryRatio = _splitterPreviewPrimaryRatio ?? _activeThread.PrimarySplitRatio;
            double secondaryRatio = _splitterPreviewSecondaryRatio ?? _activeThread.SecondarySplitRatio;
            double splitWidth = Math.Max(0, totalWidth - PaneDividerThickness);
            double splitHeight = Math.Max(0, totalHeight - PaneDividerThickness);
            double leftWidth = splitWidth * primaryRatio;
            double rightWidth = splitWidth - leftWidth;
            double topHeight = splitHeight * secondaryRatio;
            double bottomHeight = splitHeight - topHeight;

            switch (visiblePanes.Count)
            {
                case 1:
                    result.Add((visiblePanes[0], new Rect(0, 0, totalWidth, totalHeight)));
                    break;
                case 2:
                    result.Add((visiblePanes[0], new Rect(0, 0, leftWidth, totalHeight)));
                    result.Add((visiblePanes[1], new Rect(leftWidth + PaneDividerThickness, 0, rightWidth, totalHeight)));
                    break;
                case 3:
                    result.Add((visiblePanes[0], new Rect(0, 0, leftWidth, totalHeight)));
                    result.Add((visiblePanes[1], new Rect(leftWidth + PaneDividerThickness, 0, rightWidth, topHeight)));
                    result.Add((visiblePanes[2], new Rect(leftWidth + PaneDividerThickness, topHeight + PaneDividerThickness, rightWidth, bottomHeight)));
                    break;
                default:
                    result.Add((visiblePanes[0], new Rect(0, 0, leftWidth, topHeight)));
                    result.Add((visiblePanes[1], new Rect(leftWidth + PaneDividerThickness, 0, rightWidth, topHeight)));
                    result.Add((visiblePanes[2], new Rect(0, topHeight + PaneDividerThickness, leftWidth, bottomHeight)));
                    result.Add((visiblePanes[3], new Rect(leftWidth + PaneDividerThickness, topHeight + PaneDividerThickness, rightWidth, bottomHeight)));
                    break;
            }

            return result;
        }

        private static Rect InsetPreviewRect(Rect bounds, double inset)
        {
            double safeInsetX = Math.Min(inset, bounds.Width / 2);
            double safeInsetY = Math.Min(inset, bounds.Height / 2);
            return new Rect(
                bounds.X + safeInsetX,
                bounds.Y + safeInsetY,
                Math.Max(0, bounds.Width - (safeInsetX * 2)),
                Math.Max(0, bounds.Height - (safeInsetY * 2)));
        }

        private static void SetPaneSplitPreviewTransform(UIElement element, double x, double y)
        {
            if (element is null)
            {
                return;
            }

            if (Math.Abs(x) < 0.01 && Math.Abs(y) < 0.01)
            {
                element.RenderTransform = null;
                return;
            }

            if (element.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            transform.X = x;
            transform.Y = y;
        }

        private void ResetPaneSplitPreview()
        {
            ApplyPaneSplitPreview(0, 0);
        }

        private void CompletePaneSplitterInteraction(Border splitter, bool commitPreview)
        {
            ApplyPaneSplitterVisual(splitter, emphasized: false);

            if (commitPreview)
            {
                CommitPaneSplitPreviewRatios();
            }

            ResetPaneSplitPreview();
            HidePaneSplitPreview();
            ClearActiveSplitterTracking();
            RequestLayoutForVisiblePanes();
        }

        private void CommitPaneSplitPreviewRatios()
        {
            if (_activeThread is null)
            {
                return;
            }

            bool changed = false;

            if (_splitterPreviewPrimaryRatio is double previewPrimary &&
                Math.Abs(previewPrimary - _activeThread.PrimarySplitRatio) > 0.0005)
            {
                _activeThread.PrimarySplitRatio = ClampPaneSplitRatio(previewPrimary);
                changed = true;
            }

            if (_splitterPreviewSecondaryRatio is double previewSecondary &&
                Math.Abs(previewSecondary - _activeThread.SecondarySplitRatio) > 0.0005)
            {
                _activeThread.SecondarySplitRatio = ClampPaneSplitRatio(previewSecondary);
                changed = true;
            }

            ApplyCommittedPaneSplitRatios();

            if (!changed)
            {
                return;
            }

            QueueProjectTreeRefresh();
            QueueSessionSave();
            LogAutomationEvent("render", "pane.split_resized", "Updated pane split ratios", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["primarySplitRatio"] = _activeThread.PrimarySplitRatio.ToString("0.000"),
                ["secondarySplitRatio"] = _activeThread.SecondarySplitRatio.ToString("0.000"),
            });
        }

        private void ApplyCommittedPaneSplitRatios()
        {
            if (_activeThread is null)
            {
                return;
            }

            if (PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                PaneWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(_activeThread.PrimarySplitRatio, GridUnitType.Star);
                PaneWorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1 - _activeThread.PrimarySplitRatio, GridUnitType.Star);
            }

            if (PaneWorkspaceGrid.RowDefinitions.Count >= 3)
            {
                PaneWorkspaceGrid.RowDefinitions[0].Height = new GridLength(_activeThread.SecondarySplitRatio, GridUnitType.Star);
                PaneWorkspaceGrid.RowDefinitions[2].Height = new GridLength(1 - _activeThread.SecondarySplitRatio, GridUnitType.Star);
            }

            PaneWorkspaceGrid.UpdateLayout();
        }

        private void EqualizeVisiblePaneSplits(WorkspaceThread thread, bool equalizePrimary, bool equalizeSecondary, string reason)
        {
            if (thread is null)
            {
                return;
            }

            int visiblePaneCount = GetVisiblePanes(thread).Count();
            bool updated = false;

            if (equalizePrimary && visiblePaneCount >= 2)
            {
                thread.PrimarySplitRatio = 0.5;
                updated = true;
            }

            if (equalizeSecondary && visiblePaneCount >= 3)
            {
                thread.SecondarySplitRatio = 0.5;
                updated = true;
            }

            if (!updated)
            {
                return;
            }

            if (ReferenceEquals(thread, _activeThread))
            {
                RenderPaneWorkspace();
                RequestLayoutForVisiblePanes();
            }

            QueueProjectTreeRefresh();
            QueueSessionSave();
            LogAutomationEvent("render", "pane.split_equalized", "Equalized visible pane splits", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = FindProjectForThread(thread)?.Id ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["visiblePaneCount"] = visiblePaneCount.ToString(),
                ["primarySplitRatio"] = thread.PrimarySplitRatio.ToString("0.000"),
                ["secondarySplitRatio"] = thread.SecondarySplitRatio.ToString("0.000"),
            });
        }

        private void OnPaneContainerPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is WorkspacePaneRecord pane)
            {
                bool focusPane = !ShouldDeferPaneFocus(e.OriginalSource as DependencyObject);
                SelectPane(pane, focusPane);
            }
        }

        private void OnPaneZoomButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is WorkspacePaneRecord pane)
            {
                TogglePaneZoom(pane);
            }
        }

        private static bool ShouldDeferPaneFocus(DependencyObject source)
        {
            DependencyObject current = source;
            while (current is not null)
            {
                switch (current)
                {
                    case Button:
                    case HyperlinkButton:
                    case TextBox:
                    case AutoSuggestBox:
                    case Microsoft.UI.Xaml.Controls.WebView2:
                        return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void TogglePaneZoom(WorkspacePaneRecord pane)
        {
            if (_activeThread is null || pane is null)
            {
                return;
            }

            bool isZoomed = string.Equals(_activeThread.ZoomedPaneId, pane.Id, StringComparison.Ordinal);
            _activeThread.ZoomedPaneId = isZoomed ? null : pane.Id;
            _lastPaneWorkspaceRenderKey = null;
            RenderPaneWorkspace();
            RequestLayoutForVisiblePanes();
            UpdateHeader();
            QueueSessionSave();
            LogAutomationEvent("render", isZoomed ? "pane.zoom_reset" : "pane.zoomed", isZoomed ? "Restored pane layout" : "Focused pane in workspace", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
            });
        }

        private void SelectPane(WorkspacePaneRecord pane, bool focusPane = true)
        {
            if (_activeThread is null || pane is null)
            {
                return;
            }

            ClearPaneAttention(pane);
            bool alreadySelected = string.Equals(_activeThread.SelectedPaneId, pane.Id, StringComparison.Ordinal);
            if (alreadySelected)
            {
                if (focusPane)
                {
                    FocusSelectedPane();
                }

                return;
            }

            _activeThread.SelectedPaneId = pane.Id;
            _ignoreNonSelectedPaneInteractionUntil = DateTimeOffset.UtcNow.Add(CrossPaneInteractionSuppressionWindow);
            if (pane is not TerminalPaneRecord)
            {
                CancelPendingTerminalFocusRequests(_activeThread, pane.Id);
            }

            EnsureThreadPanesMaterialized(_activeProject, _activeThread);
            pane = GetSelectedPane(_activeThread) ?? pane;
            UpdateTabViewItem(pane);
            if (!string.IsNullOrWhiteSpace(_activeThread.ZoomedPaneId) &&
                !string.Equals(_activeThread.ZoomedPaneId, pane.Id, StringComparison.Ordinal))
            {
                _activeThread.ZoomedPaneId = pane.Id;
                _lastPaneWorkspaceRenderKey = null;
                RenderPaneWorkspace();
            }

            if (_tabItemsById.TryGetValue(pane.Id, out TabViewItem item) && !ReferenceEquals(TerminalTabs.SelectedItem, item))
            {
                int selectionGeneration = ++_tabSelectionChangeGeneration;
                bool previousSuppression = _suppressTabSelectionChanged;
                _suppressTabSelectionChanged = true;
                TerminalTabs.SelectedItem = item;
                RestoreTabSelectionFlagsAsync(selectionGeneration, previousSuppression, _refreshingTabView);
            }

            UpdatePaneSelectionChrome();
            SyncInspectorSectionWithSelectedPane();
            RefreshInspectorFileBrowser();
            if (focusPane)
            {
                FocusSelectedPane();
            }

            RequestLayoutForVisiblePanes();
            QueueVisibleDeferredPaneMaterialization(_activeProject, _activeThread);
            LogAutomationEvent("shell", "pane.selected", $"Selected pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["focusPane"] = focusPane.ToString(),
            });
        }

        private void CancelPendingTerminalFocusRequests(WorkspaceThread thread, string selectedPaneId)
        {
            if (thread is null)
            {
                return;
            }

            foreach (TerminalPaneRecord terminalPane in thread.Panes.OfType<TerminalPaneRecord>())
            {
                if (string.Equals(terminalPane.Id, selectedPaneId, StringComparison.Ordinal))
                {
                    continue;
                }

                terminalPane.Terminal.CancelPendingProgrammaticFocus();
            }
        }

        private void UpdatePaneSelectionChrome()
        {
            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread);
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            foreach ((string paneId, Border border) in _paneContainersById)
            {
                if (border.Tag is WorkspacePaneRecord taggedPane)
                {
                    UpdatePaneZoomButtonState(border, taggedPane);
                }

                bool isSelected = string.Equals(selectedPane?.Id, paneId, StringComparison.Ordinal);
                string accentKey = border.Tag is WorkspacePaneRecord chromePane
                    ? ResolvePaneAccentBrushKey(chromePane.Kind)
                    : "ShellPaneActiveBorderBrush";
                Brush accentBrush = AppBrush(border, accentKey);
                border.Background = isSelected
                    ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x18 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : AppBrush(border, "ShellSurfaceBackgroundBrush");
                border.BorderBrush = isSelected
                    ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x86 : (byte)0x70, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : AppBrush(border, "ShellBorderBrush");
                border.BorderThickness = new Thickness(1, 0, 1, 1);
            }

            foreach ((_, TabViewItem item) in _tabItemsById)
            {
                if (item.Tag is WorkspacePaneRecord pane)
                {
                    UpdatePaneTabChrome(item, pane);
                }
            }
        }

        private static double ClampPaneSplitRatio(double ratio)
        {
            return Math.Clamp(ratio, MinPaneSplitRatio, MaxPaneSplitRatio);
        }

        private IEnumerable<WorkspacePaneRecord> GetVisiblePanes(WorkspaceThread thread)
        {
            if (thread is null || thread.Panes.Count == 0)
            {
                return Enumerable.Empty<WorkspacePaneRecord>();
            }

            if (!string.IsNullOrWhiteSpace(thread.ZoomedPaneId))
            {
                WorkspacePaneRecord zoomedPane = thread.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.ZoomedPaneId, StringComparison.Ordinal));
                if (zoomedPane is not null)
                {
                    return new[] { zoomedPane };
                }

                thread.ZoomedPaneId = null;
            }

            int capacity = Math.Min(thread.VisiblePaneCapacity, thread.Panes.Count);
            if (capacity <= 0)
            {
                capacity = 1;
            }

            int selectedIndex = thread.Panes.FindIndex(candidate => candidate.Id == thread.SelectedPaneId);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            int start = Math.Max(0, selectedIndex - (capacity - 1));
            if (start + capacity > thread.Panes.Count)
            {
                start = Math.Max(0, thread.Panes.Count - capacity);
            }

            return thread.Panes.Skip(start).Take(capacity);
        }

        private static WorkspacePaneRecord GetSelectedPane(WorkspaceThread thread)
        {
            return thread?.Panes.FirstOrDefault(candidate => candidate.Id == thread.SelectedPaneId)
                ?? thread?.Panes.FirstOrDefault();
        }

        private void RequestLayoutForVisiblePanes()
        {
            if (_activeThread is null)
            {
                return;
            }

            _paneLayoutTimer.Stop();
            _paneLayoutTimer.Start();
        }

        private void OnPaneLayoutTimerTick(DispatcherQueueTimer sender, object args)
        {
            _paneLayoutTimer.Stop();
            List<WorkspacePaneRecord> visiblePanes = _activeThread is null
                ? new List<WorkspacePaneRecord>()
                : GetVisiblePanes(_activeThread).ToList();
            var perfData = new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
                ["selectedPaneId"] = _activeThread?.SelectedPaneId ?? string.Empty,
                ["visiblePaneCount"] = visiblePanes.Count.ToString(),
                ["reason"] = "layout-timer",
            };
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("pane.layout.apply", data: perfData);
            NativeAutomationDiagnostics.IncrementCounter("paneLayout.count");
            foreach (WorkspacePaneRecord pane in visiblePanes)
            {
                pane.RequestLayout();
            }
        }

        private void SyncAutoFitStateForVisiblePanes(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                switch (pane)
                {
                    case EditorPaneRecord editorPane:
                        editorPane.Editor.SetAutoFitWidth(thread.AutoFitPaneContentLocked);
                        break;
                    case DiffPaneRecord diffPane:
                        diffPane.DiffPane.SetAutoFitWidth(thread.AutoFitPaneContentLocked);
                        break;
                }
            }
        }

        private void ApplyFitToVisiblePanes(WorkspaceThread thread, bool persistLockState, bool autoLock, string reason)
        {
            if (thread is null)
            {
                return;
            }

            if (persistLockState)
            {
                thread.AutoFitPaneContentLocked = autoLock;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                ApplyFitToPane(pane, autoLock);
            }

            if (ReferenceEquals(thread, _activeThread))
            {
                UpdateSidebarActions();
            }

            if (persistLockState)
            {
                QueueSessionSave();
            }

            LogAutomationEvent("render", "pane.fit_applied", "Applied fit-to-width for visible panes", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = FindProjectForThread(thread)?.Id ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["autoLock"] = autoLock.ToString(),
                ["persisted"] = persistLockState.ToString(),
            });
        }

        private static void ApplyFitToPane(WorkspacePaneRecord pane, bool autoLock)
        {
            switch (pane)
            {
                case EditorPaneRecord editorPane:
                    editorPane.Editor.ApplyFitToWidth(autoLock);
                    break;
                case DiffPaneRecord diffPane:
                    diffPane.DiffPane.ApplyFitToWidth(autoLock);
                    break;
            }
        }

        private static string BuildOverviewPaneLabel(WorkspacePaneRecord pane)
        {
            string kind = pane.Kind switch
            {
                WorkspacePaneKind.Browser => "Web",
                WorkspacePaneKind.Editor => "Edit",
                WorkspacePaneKind.Diff => "Diff",
                _ => "Term",
            };

            string title = string.IsNullOrWhiteSpace(pane.Title) ? string.Empty : pane.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return kind;
            }

            string normalized = pane.Kind switch
            {
                WorkspacePaneKind.Browser when title.StartsWith("Web ", StringComparison.OrdinalIgnoreCase) => title[4..],
                WorkspacePaneKind.Diff when title.StartsWith("Diff ", StringComparison.OrdinalIgnoreCase) => title[5..],
                _ => title,
            };

            string compact = normalized.Length > 18 ? normalized[..15] + "..." : normalized;
            return $"{kind} {compact}";
        }

        private string BuildPaneWorkspaceRenderKey()
        {
            StringBuilder builder = new();
            builder.Append(_showingSettings ? '1' : '0')
                .Append('|')
                .Append(_activeThread?.Id)
                .Append('|');

            if (_activeThread is null)
            {
                return builder.ToString();
            }

            builder.Append(_activeThread.ZoomedPaneId)
                .Append('|')
                .Append(_activeThread.LayoutPreset)
                .Append('|')
                .Append(_activeThread.PrimarySplitRatio.ToString("0.000"))
                .Append('|')
                .Append(_activeThread.SecondarySplitRatio.ToString("0.000"))
                .Append('|');

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                builder.Append(pane.Id)
                    .Append(':')
                    .Append(pane.IsDeferred ? '1' : '0')
                    .Append('|');
            }

            return builder.ToString();
        }
    }
}
