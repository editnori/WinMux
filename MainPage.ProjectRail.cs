using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SelfContainedDeployment
{
    public partial class MainPage
    {
        private sealed class ThreadActivitySummary
        {
            public string Label { get; init; }

            public string ToolTip { get; init; }

            public bool IsRunning { get; init; }

            public bool RequiresAttention { get; init; }
        }

        private sealed class ProjectRailProjectMetrics
        {
            public string MetaText { get; init; }

            public bool HasRunning { get; init; }

            public bool HasAttention { get; init; }

            public bool HasChanges { get; init; }
        }

        private sealed class ProjectRailThreadMetrics
        {
            public ThreadActivitySummary Activity { get; init; }

            public IReadOnlyList<WorkspacePaneRecord> VisiblePanes { get; init; } = Array.Empty<WorkspacePaneRecord>();

            public int HiddenPaneCount { get; init; }

            public string PaneSummary { get; init; } = "No visible panes";
        }

        private sealed class ProjectRailRenderSnapshot
        {
            public string RenderKey { get; set; } = string.Empty;

            public Dictionary<string, ProjectRailProjectMetrics> ProjectMetricsById { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, ProjectRailThreadMetrics> ThreadMetricsById { get; } = new(StringComparer.Ordinal);
        }

        private void RefreshProjectTree()
        {
            ProjectRailRenderSnapshot snapshot = BuildProjectRailRenderSnapshot();
            string renderKey = snapshot.RenderKey;
            bool cacheHit = string.Equals(renderKey, _lastProjectTreeRenderKey, StringComparison.Ordinal);
            var perfData = new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
                ["projectCount"] = _projects.Count.ToString(),
                ["paneOpen"] = (ShellSplitView?.IsPaneOpen == true).ToString(),
                ["showingSettings"] = _showingSettings.ToString(),
                ["cacheHit"] = cacheHit.ToString(),
                ["renderKey"] = renderKey,
            };
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("render.project-tree", data: perfData);
            NativeAutomationDiagnostics.IncrementCounter("projectTree.refreshCount");
            if (cacheHit)
            {
                return;
            }

            _lastProjectTreeRenderKey = renderKey;
            ProjectListPanel.Children.Clear();
            _projectButtonsById.Clear();
            _projectHeaderBordersById.Clear();
            _threadButtonsById.Clear();
            _threadActivitySummariesById.Clear();
            _hoveredProjectIds.Clear();
            _hoveredThreadIds.Clear();
            bool isOpen = ShellSplitView.IsPaneOpen;

            foreach (WorkspaceProject project in _projects)
            {
                bool showProjectThreads = isOpen && ReferenceEquals(project, _activeProject);
                StackPanel group = new()
                {
                    Spacing = 3,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(group, $"shell-project-group-{project.Id}");

                Button projectButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellSidebarProjectButtonStyle"],
                    Tag = project.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(projectButton, $"shell-project-{project.Id}");
                AutomationProperties.SetName(projectButton, project.Name);
                projectButton.Click += OnProjectButtonClicked;
                ToolTipService.SetToolTip(projectButton, FormatProjectPath(project));
                _projectButtonsById[project.Id] = projectButton;

                Grid projectLayout = new()
                {
                    ColumnSpacing = 5,
                };
                AutomationProperties.SetAutomationId(projectLayout, $"shell-project-layout-{project.Id}");
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                FontIcon projectIcon = new()
                {
                    FontSize = 11.5,
                    Glyph = "\uE8B7",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = AppBrush(projectLayout, ResolveProjectRailIconBrushKey(project, snapshot)),
                };
                AutomationProperties.SetAutomationId(projectIcon, $"shell-project-icon-{project.Id}");
                projectLayout.Children.Add(projectIcon);

                if (isOpen)
                {
                    projectButton.Height = double.NaN;
                    projectButton.MinHeight = 28;
                    projectButton.Width = double.NaN;
                    projectButton.Padding = new Thickness(4, 4, 4, 4);
                    projectButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                    projectButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    StackPanel textStack = new()
                    {
                        Spacing = 0,
                    };
                    AutomationProperties.SetAutomationId(textStack, $"shell-project-text-{project.Id}");
                    Grid.SetColumn(textStack, 1);
                    TextBlock projectTitle = new()
                    {
                        Text = project.Name,
                        Style = (Style)Application.Current.Resources["ShellSidebarTitleTextStyle"],
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectTitle, $"shell-project-title-{project.Id}");
                    textStack.Children.Add(projectTitle);
                    TextBlock projectMeta = new()
                    {
                        Text = ResolveProjectRailMeta(project, snapshot),
                        Style = (Style)Application.Current.Resources["ShellSidebarMetaTextStyle"],
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectMeta, $"shell-project-meta-{project.Id}");
                    ToolTipService.SetToolTip(projectMeta, FormatProjectPath(project));
                    textStack.Children.Add(projectMeta);
                    projectLayout.Children.Add(textStack);
                    projectButton.Content = projectLayout;
                }
                else
                {
                    projectButton.MinHeight = 32;
                    projectButton.Height = 32;
                    projectButton.Width = 32;
                    projectButton.Padding = new Thickness(0);
                    projectButton.HorizontalAlignment = HorizontalAlignment.Left;
                    projectButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                    projectButton.Margin = new Thickness(6, 0, 0, 0);
                    projectButton.PointerEntered += OnProjectHeaderPointerEntered;
                    projectButton.PointerExited += OnProjectHeaderPointerExited;
                    projectButton.Content = new Border
                    {
                        Width = 20,
                        Height = 20,
                        CornerRadius = new CornerRadius(4),
                        Background = AppBrush(projectButton, "ShellBrandMarkBackgroundBrush"),
                        BorderBrush = AppBrush(projectButton, "ShellBrandMarkBorderBrush"),
                        BorderThickness = new Thickness(1),
                        Child = new FontIcon
                        {
                            FontSize = 11.5,
                            Glyph = "\uE8B7",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = AppBrush(projectButton, ResolveProjectRailIconBrushKey(project, snapshot)),
                        },
                    };
                }

                MenuFlyout projectMenu = new();
                MenuFlyoutItem projectNewThreadItem = new()
                {
                    Text = "New thread",
                    Tag = project.Id,
                };
                projectNewThreadItem.Click += OnProjectNewThreadMenuClicked;
                projectMenu.Items.Add(projectNewThreadItem);
                MenuFlyoutItem clearThreadsItem = new()
                {
                    Text = "Clear all threads",
                    Tag = project.Id,
                    IsEnabled = project.Threads.Count > 0,
                };
                clearThreadsItem.Click += OnClearProjectThreadsMenuClicked;
                projectMenu.Items.Add(clearThreadsItem);
                MenuFlyoutItem deleteProjectItem = new()
                {
                    Text = "Remove project",
                    Tag = project.Id,
                };
                deleteProjectItem.Click += OnDeleteProjectMenuClicked;
                projectMenu.Items.Add(deleteProjectItem);
                projectButton.ContextFlyout = projectMenu;
                if (!isOpen)
                {
                    ApplyProjectRowState(project.Id, project == _activeProject && !_showingSettings);
                    group.Children.Add(projectButton);
                    ProjectListPanel.Children.Add(group);
                    continue;
                }

                Button addThreadButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellGhostToolbarButtonStyle"],
                    Tag = project.Id,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 18,
                    Height = 18,
                    Opacity = 0.62,
                };
                AutomationProperties.SetAutomationId(addThreadButton, $"shell-project-add-thread-{project.Id}");
                AutomationProperties.SetName(addThreadButton, $"Add thread to {project.Name}");
                addThreadButton.Click += OnProjectAddThreadClicked;
                addThreadButton.Foreground = AppBrush(addThreadButton, "ShellTextSecondaryBrush");
                ToolTipService.SetToolTip(addThreadButton, "Add thread");
                addThreadButton.Content = new FontIcon
                {
                    FontSize = 10.5,
                    Glyph = "\uE710",
                };

                Grid projectHeader = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    ColumnSpacing = 4,
                };
                AutomationProperties.SetAutomationId(projectHeader, $"shell-project-header-{project.Id}");
                projectHeader.Children.Add(projectButton);
                Grid.SetColumn(projectButton, 0);
                Grid.SetColumn(addThreadButton, 1);
                projectHeader.Children.Add(addThreadButton);

                Border projectHeaderChrome = new()
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                    CornerRadius = new CornerRadius(2),
                    Child = projectHeader,
                    Tag = project.Id,
                };
                AutomationProperties.SetAutomationId(projectHeaderChrome, $"shell-project-header-chrome-{project.Id}");
                projectHeaderChrome.PointerEntered += OnProjectHeaderPointerEntered;
                projectHeaderChrome.PointerExited += OnProjectHeaderPointerExited;
                _projectHeaderBordersById[project.Id] = projectHeaderChrome;
                ApplyProjectRowState(project.Id, project == _activeProject && !_showingSettings);
                group.Children.Add(projectHeaderChrome);

                if (showProjectThreads)
                {
                    StackPanel threadStack = new()
                    {
                        Spacing = 2,
                        Margin = new Thickness(6, 2, 0, 2),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    AutomationProperties.SetAutomationId(threadStack, $"shell-thread-list-{project.Id}");

                    foreach (WorkspaceThread thread in project.Threads)
                    {
                        ProjectRailThreadMetrics threadMetrics = ResolveProjectRailThreadMetrics(snapshot, thread);
                        ThreadActivitySummary activitySummary = threadMetrics?.Activity;
                        Button threadButton = new()
                        {
                            Style = (Style)Application.Current.Resources["ShellSidebarThreadButtonStyle"],
                            Tag = thread.Id,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Height = double.NaN,
                            MinHeight = 28,
                            Padding = new Thickness(3, 3, 3, 3),
                        };
                        AutomationProperties.SetAutomationId(threadButton, $"shell-thread-{thread.Id}");
                        AutomationProperties.SetName(threadButton, BuildThreadAutomationLabel(project, thread, activitySummary));
                        threadButton.Click += OnThreadButtonClicked;
                        threadButton.DoubleTapped += OnThreadButtonDoubleTapped;
                        threadButton.PointerEntered += OnThreadButtonPointerEntered;
                        threadButton.PointerExited += OnThreadButtonPointerExited;
                        ToolTipService.SetToolTip(threadButton, BuildThreadButtonToolTip(project, thread, threadMetrics?.PaneSummary ?? "No visible panes"));
                        _threadButtonsById[thread.Id] = threadButton;
                        _threadActivitySummariesById[thread.Id] = activitySummary;

                        MenuFlyout threadMenu = new();
                        MenuFlyoutItem renameItem = new()
                        {
                            Text = "Rename",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(renameItem, $"shell-thread-rename-{thread.Id}");
                        renameItem.Click += OnRenameThreadMenuClicked;
                        MenuFlyoutItem editNoteItem = new()
                        {
                            Text = "Notes",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(editNoteItem, $"shell-thread-note-{thread.Id}");
                        editNoteItem.Click += OnEditThreadNotesMenuClicked;
                        MenuFlyoutItem newNoteItem = new()
                        {
                            Text = "New note",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(newNoteItem, $"shell-thread-note-new-{thread.Id}");
                        newNoteItem.Click += OnNewThreadNoteMenuClicked;
                        MenuFlyoutItem duplicateItem = new()
                        {
                            Text = "Duplicate",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(duplicateItem, $"shell-thread-duplicate-{thread.Id}");
                        duplicateItem.Click += OnDuplicateThreadMenuClicked;
                        MenuFlyoutItem deleteItem = new()
                        {
                            Text = "Clear thread",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(deleteItem, $"shell-thread-delete-{thread.Id}");
                        deleteItem.Click += OnDeleteThreadMenuClicked;
                        threadMenu.Items.Add(renameItem);
                        threadMenu.Items.Add(editNoteItem);
                        threadMenu.Items.Add(newNoteItem);
                        threadMenu.Items.Add(duplicateItem);
                        threadMenu.Items.Add(deleteItem);
                        threadButton.ContextFlyout = threadMenu;

                        Grid threadLayout = new()
                        {
                            ColumnSpacing = 5,
                        };
                        AutomationProperties.SetAutomationId(threadLayout, $"shell-thread-layout-{thread.Id}");
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        FontIcon threadIcon = new()
                        {
                            FontSize = 10.8,
                            Glyph = "\uE8BD",
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = AppBrush(threadLayout, ResolveThreadRailIconBrushKey(thread, activitySummary)),
                        };
                        AutomationProperties.SetAutomationId(threadIcon, $"shell-thread-icon-{thread.Id}");
                        threadLayout.Children.Add(threadIcon);

                        StackPanel threadText = new()
                        {
                            Spacing = 1,
                        };
                        AutomationProperties.SetAutomationId(threadText, $"shell-thread-text-{thread.Id}");
                        Grid.SetColumn(threadText, 1);
                        TextBlock threadTitle = new()
                        {
                            Text = thread.Name,
                            Style = (Style)Application.Current.Resources["ShellSidebarTitleTextStyle"],
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        AutomationProperties.SetAutomationId(threadTitle, $"shell-thread-title-{thread.Id}");
                        threadText.Children.Add(threadTitle);

                        Grid threadFooter = new()
                        {
                            ColumnDefinitions =
                            {
                                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                                new ColumnDefinition { Width = GridLength.Auto },
                                new ColumnDefinition { Width = GridLength.Auto },
                            },
                            ColumnSpacing = 4,
                        };
                        TextBlock threadMeta = new()
                        {
                            Text = BuildThreadRailMeta(project, thread),
                            Style = (Style)Application.Current.Resources["ShellSidebarMetaTextStyle"],
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        AutomationProperties.SetAutomationId(threadMeta, $"shell-thread-meta-{thread.Id}");
                        threadFooter.Children.Add(threadMeta);

                        bool showPaneStrip = thread.Panes.Count > 0;
                        if (showPaneStrip)
                        {
                            StackPanel threadAdornments = new()
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 6,
                                VerticalAlignment = VerticalAlignment.Center,
                            };
                            AutomationProperties.SetAutomationId(threadAdornments, $"shell-thread-adornments-{thread.Id}");

                            FrameworkElement paneStrip = BuildThreadPaneStrip(
                                thread,
                                threadMetrics?.VisiblePanes ?? Array.Empty<WorkspacePaneRecord>(),
                                threadMetrics?.HiddenPaneCount ?? Math.Max(0, thread.Panes.Count));
                            AutomationProperties.SetAutomationId(paneStrip, $"shell-thread-panes-{thread.Id}");
                            threadAdornments.Children.Add(paneStrip);

                            Grid.SetColumn(threadAdornments, 1);
                            threadFooter.Children.Add(threadAdornments);
                        }

                        FrameworkElement threadStatus = BuildThreadActivityIndicator(activitySummary);
                        if (threadStatus is not null)
                        {
                            AutomationProperties.SetAutomationId(threadStatus, $"shell-thread-status-{thread.Id}");
                            Grid.SetColumn(threadStatus, 2);
                            threadFooter.Children.Add(threadStatus);
                        }

                        threadText.Children.Add(threadFooter);

                        threadLayout.Children.Add(threadText);
                        threadButton.Content = threadLayout;
                        ApplySidebarThreadButtonState(
                            threadButton,
                            thread,
                            thread == _activeThread && !_showingSettings,
                            activitySummary,
                            _hoveredThreadIds.Contains(thread.Id));
                        threadStack.Children.Add(threadButton);
                    }

                    if (project.Threads.Count == 0)
                    {
                        threadStack.Children.Add(new TextBlock
                        {
                            Text = "No threads yet",
                            Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                        });
                    }

                    group.Children.Add(threadStack);
                }

                ProjectListPanel.Children.Add(group);
            }
        }

        private void UpdateProjectTreeSelectionVisuals()
        {
            UpdateProjectTreeSelectionVisuals(previousProjectId: null, previousThreadId: null, forceAll: true);
        }

        private void UpdateProjectTreeSelectionVisuals(string previousProjectId, string previousThreadId, bool forceAll = false)
        {
            bool activeShellView = !_showingSettings;
            if (forceAll || (string.IsNullOrWhiteSpace(previousProjectId) && string.IsNullOrWhiteSpace(previousThreadId)))
            {
                foreach (string projectId in _projectButtonsById.Keys)
                {
                    ApplyProjectRowState(projectId, activeShellView && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
                }

                foreach ((string threadId, Button threadButton) in _threadButtonsById)
                {
                    _threadActivitySummariesById.TryGetValue(threadId, out ThreadActivitySummary summary);
                    WorkspaceThread thread = FindThread(threadId);
                    ApplySidebarThreadButtonState(
                        threadButton,
                        thread,
                        activeShellView && string.Equals(threadId, _activeThread?.Id, StringComparison.Ordinal),
                        summary,
                        _hoveredThreadIds.Contains(threadId));
                }

                return;
            }

            UpdateProjectTreeSelectionVisual(previousProjectId, activeShellView);
            if (!string.Equals(previousProjectId, _activeProject?.Id, StringComparison.Ordinal))
            {
                UpdateProjectTreeSelectionVisual(_activeProject?.Id, activeShellView);
            }

            UpdateThreadTreeSelectionVisual(previousThreadId, activeShellView);
            if (!string.Equals(previousThreadId, _activeThread?.Id, StringComparison.Ordinal))
            {
                UpdateThreadTreeSelectionVisual(_activeThread?.Id, activeShellView);
            }
        }

        private void UpdateProjectTreeSelectionVisual(string projectId, bool activeShellView)
        {
            if (string.IsNullOrWhiteSpace(projectId) ||
                !_projectButtonsById.ContainsKey(projectId))
            {
                return;
            }

            ApplyProjectRowState(projectId, activeShellView && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
        }

        private void UpdateThreadTreeSelectionVisual(string threadId, bool activeShellView)
        {
            if (string.IsNullOrWhiteSpace(threadId) ||
                !_threadButtonsById.TryGetValue(threadId, out Button threadButton))
            {
                return;
            }

            _threadActivitySummariesById.TryGetValue(threadId, out ThreadActivitySummary summary);
            WorkspaceThread thread = FindThread(threadId);
            ApplySidebarThreadButtonState(
                threadButton,
                thread,
                activeShellView && string.Equals(threadId, _activeThread?.Id, StringComparison.Ordinal),
                summary,
                _hoveredThreadIds.Contains(threadId));
        }

        private void OnProjectHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string projectId)
            {
                return;
            }

            _hoveredProjectIds.Add(projectId);
            ApplyProjectRowState(projectId, !_showingSettings && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
        }

        private void OnProjectHeaderPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string projectId)
            {
                return;
            }

            _hoveredProjectIds.Remove(projectId);
            ApplyProjectRowState(projectId, !_showingSettings && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
        }

        private void OnThreadButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string threadId)
            {
                return;
            }

            _hoveredThreadIds.Add(threadId);
            UpdateThreadTreeSelectionVisual(threadId, !_showingSettings);
        }

        private void OnThreadButtonPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string threadId)
            {
                return;
            }

            _hoveredThreadIds.Remove(threadId);
            UpdateThreadTreeSelectionVisual(threadId, !_showingSettings);
        }

        private void ApplyProjectRowState(string projectId, bool active)
        {
            if (!_projectButtonsById.TryGetValue(projectId, out Button projectButton))
            {
                return;
            }

            bool hovered = _hoveredProjectIds.Contains(projectId);

            if (_projectHeaderBordersById.TryGetValue(projectId, out Border projectHeaderChrome))
            {
                projectHeaderChrome.Background = active
                    ? CreateSidebarTintedBrush(AppBrush(projectHeaderChrome, "ShellPaneActiveBorderBrush"), hovered ? (byte)0x18 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : hovered
                        ? AppBrush(projectHeaderChrome, "ShellNavHoverBrush")
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                projectHeaderChrome.BorderThickness = new Thickness(0);
                projectHeaderChrome.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                projectButton.Background = null;
                projectButton.BorderBrush = null;
                projectButton.Foreground = AppBrush(projectButton, "ShellTextPrimaryBrush");
                return;
            }

            ApplyProjectButtonState(projectButton, active, hovered);
        }

        private static string FormatProjectRailMeta(int threadCount, int liveCount, int readyCount)
        {
            List<string> parts = new()
            {
                threadCount == 0
                    ? "No threads yet"
                    : $"{threadCount} thread{(threadCount == 1 ? string.Empty : "s")}",
            };
            if (liveCount > 0)
            {
                parts.Add($"{liveCount} active");
            }

            if (readyCount > 0)
            {
                parts.Add($"{readyCount} ready");
            }

            return string.Join(" · ", parts);
        }

        private static string ResolveProjectRailMeta(WorkspaceProject project, ProjectRailRenderSnapshot snapshot)
        {
            if (project is null)
            {
                return "No project";
            }

            return snapshot?.ProjectMetricsById.TryGetValue(project.Id, out ProjectRailProjectMetrics metrics) == true
                ? metrics.MetaText
                : FormatProjectRailMeta(project.Threads.Count, liveCount: 0, readyCount: 0);
        }

        private static ThreadActivitySummary ResolveThreadActivitySummary(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return null;
            }

            int attentionCount = 0;
            int activeToolCount = 0;
            TerminalPaneRecord representativeAttentionToolPane = null;
            TerminalPaneRecord representativeActiveToolPane = null;

            foreach (WorkspacePaneRecord pane in thread.Panes)
            {
                bool isAttentionPane = pane.RequiresAttention;
                if (isAttentionPane)
                {
                    attentionCount++;
                }

                if (pane is not TerminalPaneRecord terminalPane)
                {
                    continue;
                }

                bool hasLiveTool = terminalPane.Terminal?.HasLiveToolSession == true &&
                    !terminalPane.IsExited &&
                    !terminalPane.ReplayRestoreFailed;
                if (hasLiveTool)
                {
                    activeToolCount++;
                    representativeActiveToolPane ??= terminalPane;
                }

                if (isAttentionPane &&
                    representativeAttentionToolPane is null &&
                    (!string.IsNullOrWhiteSpace(terminalPane.Terminal?.ActiveToolSession) ||
                     !string.IsNullOrWhiteSpace(terminalPane.ReplayTool)))
                {
                    representativeAttentionToolPane = terminalPane;
                }
            }

            if (attentionCount > 0)
            {
                TerminalPaneRecord attentionToolPane = representativeAttentionToolPane ?? representativeActiveToolPane;
                string toolName = ResolveToolName(attentionToolPane?.Terminal?.ActiveToolSession)
                    ?? ResolveToolName(attentionToolPane?.ReplayTool);
                return new ThreadActivitySummary
                {
                    Label = string.IsNullOrWhiteSpace(toolName)
                        ? (attentionCount == 1 ? "Ready" : $"{attentionCount} ready")
                        : toolName,
                    ToolTip = string.IsNullOrWhiteSpace(toolName)
                        ? $"{attentionCount} pane{(attentionCount == 1 ? string.Empty : "s")} have unread activity."
                        : $"{toolName} has unread activity.",
                    RequiresAttention = true,
                };
            }

            if (activeToolCount > 0)
            {
                string toolName = activeToolCount == 1
                    ? ResolveToolName(representativeActiveToolPane?.Terminal?.ActiveToolSession) ?? ResolveToolName(representativeActiveToolPane?.ReplayTool) ?? "Agent"
                    : $"{activeToolCount} live";
                return new ThreadActivitySummary
                {
                    Label = toolName,
                    ToolTip = activeToolCount == 1
                        ? $"{toolName} session is active."
                        : $"{activeToolCount} agent sessions are active.",
                    IsRunning = true,
                };
            }

            return null;
        }

        private static string ResolveToolName(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "claude" => "Claude",
                "codex" => "Codex",
                _ => null,
            };
        }

        private static string BuildThreadAutomationLabel(WorkspaceProject project, WorkspaceThread thread, ThreadActivitySummary summary)
        {
            StringBuilder builder = new();
            builder.Append(thread?.Name ?? "Thread");
            if (project is not null && thread is not null)
            {
                builder.Append(' ')
                    .Append(BuildThreadRailMeta(project, thread));
            }

            if (summary?.RequiresAttention == true)
            {
                builder.Append(' ')
                    .Append(summary.Label)
                    .Append(" ready");
            }
            else if (summary?.IsRunning == true)
            {
                builder.Append(' ')
                    .Append(summary.Label)
                    .Append(" active");
            }

            int noteCount = thread?.NoteEntries.Count ?? 0;
            if (noteCount > 0)
            {
                builder.Append(' ')
                    .Append(noteCount)
                    .Append(" note");
                if (noteCount != 1)
                {
                    builder.Append('s');
                }
            }

            return builder.ToString();
        }

        private static string BuildThreadRailMeta(WorkspaceProject project, WorkspaceThread thread)
        {
            string worktreeName = ShellProfiles.DeriveName(thread.WorktreePath ?? project.RootPath);
            string location = string.IsNullOrWhiteSpace(thread.BranchName)
                ? worktreeName
                : thread.BranchName;

            if (location?.Length > 20)
            {
                location = location[..17] + "...";
            }

            string meta = thread.ChangedFileCount <= 0
                ? location
                : $"{location} · {thread.ChangedFileCount} files";
            int noteCount = thread?.NoteEntries.Count ?? 0;
            if (noteCount > 0)
            {
                meta += $" · {noteCount} note{(noteCount == 1 ? string.Empty : "s")}";
            }

            return meta;
        }

        private static string BuildThreadButtonToolTip(WorkspaceProject project, WorkspaceThread thread, string paneSummary)
        {
            StringBuilder builder = new();
            builder.Append(FormatThreadPath(project, thread))
                .Append(" · ")
                .Append(paneSummary);

            int noteCount = thread?.NoteEntries.Count ?? 0;
            string notePreview = BuildThreadNotePreview(ResolvePreferredThreadNote(thread)?.Text, maxLength: 120);
            if (noteCount > 0)
            {
                builder.Append(Environment.NewLine)
                    .Append(noteCount == 1 ? "Note: " : $"{noteCount} notes: ");
                if (!string.IsNullOrWhiteSpace(notePreview))
                {
                    builder.Append(notePreview);
                }
            }

            return builder.ToString();
        }

        private static ProjectRailThreadMetrics ResolveProjectRailThreadMetrics(ProjectRailRenderSnapshot snapshot, WorkspaceThread thread)
        {
            if (snapshot is null || thread is null)
            {
                return null;
            }

            snapshot.ThreadMetricsById.TryGetValue(thread.Id, out ProjectRailThreadMetrics metrics);
            return metrics;
        }

        private string ResolveProjectRailIconBrushKey(WorkspaceProject project, ProjectRailRenderSnapshot snapshot)
        {
            if (ReferenceEquals(project, _activeProject))
            {
                return "ShellTextPrimaryBrush";
            }

            if (project is null)
            {
                return "ShellTextTertiaryBrush";
            }

            if (snapshot?.ProjectMetricsById.TryGetValue(project.Id, out ProjectRailProjectMetrics metrics) != true)
            {
                return "ShellTextTertiaryBrush";
            }

            if (metrics.HasAttention)
            {
                return "ShellSuccessBrush";
            }

            if (metrics.HasRunning)
            {
                return "ShellInfoBrush";
            }

            if (metrics.HasChanges)
            {
                return "ShellWarningBrush";
            }

            return "ShellTextTertiaryBrush";
        }

        private static string ResolveThreadRailIconBrushKey(WorkspaceThread thread, ThreadActivitySummary summary)
        {
            if (summary?.RequiresAttention == true)
            {
                return "ShellSuccessBrush";
            }

            if (summary?.IsRunning == true)
            {
                return "ShellInfoBrush";
            }

            if (thread?.ChangedFileCount > 0)
            {
                return "ShellWarningBrush";
            }

            if ((thread?.NoteEntries.Count ?? 0) > 0)
            {
                return "ShellWarningBrush";
            }

            return "ShellTextTertiaryBrush";
        }

        private FrameworkElement BuildThreadPaneStrip(WorkspaceThread thread, IReadOnlyList<WorkspacePaneRecord> visiblePanes, int hiddenPaneCount)
        {
            StackPanel strip = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            foreach (WorkspacePaneRecord pane in visiblePanes ?? Array.Empty<WorkspacePaneRecord>())
            {
                strip.Children.Add(BuildThreadPaneBadge(pane, string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal)));
            }

            if (hiddenPaneCount > 0)
            {
                TextBlock overflowBadge = new()
                {
                    Text = $"+{hiddenPaneCount}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9.4,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppBrush(strip, "ShellTextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(overflowBadge, $"{hiddenPaneCount} additional pane{(hiddenPaneCount == 1 ? string.Empty : "s")} are hidden in this layout.");
                strip.Children.Add(overflowBadge);
            }

            return strip;
        }

        private static FrameworkElement BuildThreadPaneBadge(WorkspacePaneRecord pane, bool selected)
        {
            string accentKey = ResolvePaneAccentBrushKey(pane.Kind);
            Brush accentBrush = AppBrush(null, accentKey);
            FontIcon badge = new()
            {
                Glyph = ResolvePaneKindGlyph(pane.Kind),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 9.8,
                Foreground = accentBrush,
                Opacity = selected ? 1 : 0.76,
                Margin = new Thickness(0, 0, 4, 0),
            };
            ToolTipService.SetToolTip(badge, BuildOverviewPaneLabel(pane));
            return badge;
        }

        private static FrameworkElement BuildThreadActivityIndicator(ThreadActivitySummary summary)
        {
            if (summary is null)
            {
                return null;
            }

            string accentKey = summary.RequiresAttention ? "ShellSuccessBrush" : "ShellInfoBrush";
            TextBlock label = new()
            {
                Text = summary.Label,
                FontSize = 9.9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppBrush(null, accentKey),
                VerticalAlignment = VerticalAlignment.Center,
            };

            ToolTipService.SetToolTip(label, summary.ToolTip);
            return label;
        }

        private static string ResolveThreadSelectionBrushKey(WorkspaceThread thread, ThreadActivitySummary summary)
        {
            WorkspacePaneRecord selectedPane = thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.SelectedPaneId, StringComparison.Ordinal))
                ?? thread?.Panes.FirstOrDefault();
            if (selectedPane is not null)
            {
                return ResolvePaneAccentBrushKey(selectedPane.Kind);
            }

            if (summary?.RequiresAttention == true)
            {
                return "ShellSuccessBrush";
            }

            if (summary?.IsRunning == true)
            {
                return "ShellInfoBrush";
            }

            return "ShellPaneActiveBorderBrush";
        }

        private static void ApplySidebarThreadButtonState(Button button, WorkspaceThread thread, bool active, ThreadActivitySummary summary, bool hovered)
        {
            string accentKey = ResolveThreadSelectionBrushKey(thread, summary);
            Brush accentBrush = AppBrush(button, accentKey);
            button.BorderThickness = new Thickness(0);
            button.BorderBrush = null;

            if (active)
            {
                button.Background = CreateSidebarTintedBrush(accentBrush, hovered ? (byte)0x18 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            if (hovered)
            {
                button.Background = AppBrush(button, "ShellNavHoverBrush");
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            button.Background = null;
            button.BorderBrush = null;
            button.BorderThickness = new Thickness(0);
            button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
        }

        private static Brush CreateSidebarTintedBrush(Brush source, byte alpha, Windows.UI.Color fallbackBaseColor)
        {
            Windows.UI.Color baseColor = source is SolidColorBrush solid
                ? solid.Color
                : fallbackBaseColor;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        }

        private static string BuildOverviewPaneSummary(IReadOnlyList<WorkspacePaneRecord> visiblePanes, int hiddenPaneCount)
        {
            if (visiblePanes is null || visiblePanes.Count == 0)
            {
                return "No visible panes";
            }

            string summary = string.Join(" · ", visiblePanes.Select(BuildOverviewPaneLabel));
            if (hiddenPaneCount > 0)
            {
                summary += $" · +{hiddenPaneCount} more";
            }

            return summary;
        }

        private ProjectRailRenderSnapshot BuildProjectRailRenderSnapshot()
        {
            ProjectRailRenderSnapshot snapshot = new();
            StringBuilder builder = new();
            builder.Append(ResolveTheme(SampleConfig.CurrentTheme))
                .Append('|')
                .Append(ShellSplitView?.IsPaneOpen == true ? '1' : '0')
                .Append('|')
                .Append(_showingSettings ? '1' : '0')
                .Append('|')
                .Append(_activeProject?.Id)
                .Append('|');

            foreach (WorkspaceProject project in _projects)
            {
                int liveCount = 0;
                int readyCount = 0;
                bool hasChanges = false;
                foreach (WorkspaceThread projectThread in project.Threads)
                {
                    ThreadActivitySummary summary = ResolveThreadActivitySummary(projectThread);
                    if (summary?.IsRunning == true)
                    {
                        liveCount++;
                    }

                    if (summary?.RequiresAttention == true)
                    {
                        readyCount++;
                    }

                    hasChanges |= projectThread.ChangedFileCount > 0;

                    if (ReferenceEquals(project, _activeProject))
                    {
                        List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(projectThread).ToList();
                        int hiddenPaneCount = Math.Max(0, projectThread.Panes.Count - visiblePanes.Count);
                        snapshot.ThreadMetricsById[projectThread.Id] = new ProjectRailThreadMetrics
                        {
                            Activity = summary,
                            VisiblePanes = visiblePanes,
                            HiddenPaneCount = hiddenPaneCount,
                            PaneSummary = BuildOverviewPaneSummary(visiblePanes, hiddenPaneCount),
                        };
                    }
                }

                snapshot.ProjectMetricsById[project.Id] = new ProjectRailProjectMetrics
                {
                    MetaText = FormatProjectRailMeta(project.Threads.Count, liveCount, readyCount),
                    HasRunning = liveCount > 0,
                    HasAttention = readyCount > 0,
                    HasChanges = hasChanges,
                };

                builder.Append(project.Id)
                    .Append(':')
                    .Append(project.Name)
                    .Append(':')
                    .Append(project.Threads.Count)
                    .Append(':')
                    .Append(liveCount)
                    .Append(':')
                    .Append(readyCount)
                    .Append('|');

                if (!ReferenceEquals(project, _activeProject))
                {
                    continue;
                }

                foreach (WorkspaceThread thread in project.Threads)
                {
                    ProjectRailThreadMetrics threadMetrics = ResolveProjectRailThreadMetrics(snapshot, thread);
                    IReadOnlyList<WorkspacePaneRecord> visiblePanes = threadMetrics?.VisiblePanes ?? Array.Empty<WorkspacePaneRecord>();
                    builder.Append(thread.Id)
                        .Append(':')
                        .Append(thread.Name)
                        .Append(':')
                        .Append(thread.BranchName)
                        .Append(':')
                        .Append(thread.WorktreePath)
                        .Append(':')
                        .Append(thread.ChangedFileCount)
                        .Append(':')
                        .Append(thread.SelectedPaneId)
                        .Append(':')
                        .Append(thread.ZoomedPaneId)
                        .Append(':')
                        .Append(thread.VisiblePaneCapacity)
                        .Append(':')
                        .Append(thread.NoteEntries.Count)
                        .Append('|');

                    ThreadActivitySummary summary = threadMetrics?.Activity;
                    AppendThreadActivitySummaryKey(builder, summary);
                    builder.Append('|');

                    foreach (WorkspacePaneRecord pane in visiblePanes)
                    {
                        builder.Append(pane.Id)
                            .Append(',')
                            .Append(pane.Kind)
                            .Append(',')
                            .Append(string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal) ? '1' : '0')
                            .Append('|');
                    }

                    int hiddenPaneCount = threadMetrics?.HiddenPaneCount ?? Math.Max(0, thread.Panes.Count - visiblePanes.Count);
                    builder.Append("hidden:")
                        .Append(hiddenPaneCount)
                        .Append('|');
                }
            }

            snapshot.RenderKey = builder.ToString();
            return snapshot;
        }

        private static void AppendThreadActivitySummaryKey(StringBuilder builder, ThreadActivitySummary summary)
        {
            if (summary is null)
            {
                builder.Append("none");
                return;
            }

            builder.Append(summary.Label)
                .Append(':')
                .Append(summary.RequiresAttention ? '1' : '0')
                .Append(':')
                .Append(summary.IsRunning ? '1' : '0');
        }
    }
}
