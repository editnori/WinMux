using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Git;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SelfContainedDeployment
{
    public partial class MainPage
    {
        private FrameworkElement InspectorFilesContent => InspectorFilesView;

        private TextBlock InspectorDirectoryRootText => InspectorFilesView?.DirectoryRootText;

        private TextBlock InspectorDirectoryMetaText => InspectorFilesView?.DirectoryMetaText;

        private TreeView InspectorDirectoryTree => InspectorFilesView?.DirectoryTree;

        private TextBlock InspectorDirectoryEmptyText => InspectorFilesView?.DirectoryEmptyText;

        private void OnInspectorCollapseAllClicked(object sender, RoutedEventArgs e)
        {
            if (InspectorDirectoryTree is null)
            {
                return;
            }

            InspectorDirectoryTree.SelectedNode = null;
            foreach (TreeViewNode rootNode in InspectorDirectoryTree.RootNodes)
            {
                CollapseInspectorDirectoryNode(rootNode);
            }

            UpdateInspectorFileActionState();
        }

        private void OnInspectorDirectoryTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            MaterializeInspectorDirectoryChildren(args?.Node);
        }

        private async void OnInspectorDirectoryItemInvoked(object sender, EventArgs e)
        {
            if (ResolveSelectedInspectorDirectoryItem() is not InspectorDirectoryTreeItem item || item.IsDirectory)
            {
                return;
            }

            await OpenEditorFileFromInspectorAsync(item.RelativePath).ConfigureAwait(true);
        }

        private async void OnInspectorSaveFileClicked(object sender, RoutedEventArgs e)
        {
            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            if (editorPane is null)
            {
                return;
            }

            await editorPane.Editor.SaveCurrentFilePublicAsync().ConfigureAwait(true);
            RefreshInspectorFileBrowser();
        }

        private void RefreshInspectorFileBrowserStatus()
        {
            if (InspectorDirectoryMetaText is null)
            {
                return;
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            InspectorDirectoryMetaText.Text = editorPane is null
                ? "Select a file to open it in a new editor pane."
                : editorPane.Editor.StatusText;
            UpdateInspectorFileActionState();
        }

        private void RefreshInspectorFileBrowser(bool forceRebuild = false)
        {
            if (InspectorDirectoryTree is null || InspectorDirectoryRootText is null || InspectorDirectoryMetaText is null || InspectorDirectoryEmptyText is null)
            {
                return;
            }

            if (_showingSettings || !_inspectorOpen || _activeThread is null)
            {
                CancelPendingInspectorDirectoryBuilds();
                UpdateInspectorFileActionState();
                return;
            }

            if (_activeInspectorSection != InspectorSection.Files && !forceRebuild)
            {
                UpdateInspectorFileActionState();
                return;
            }

            string rootPath = ResolveThreadRootPath(_activeProject, _activeThread);
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                _lastInspectorDirectoryRootPath = null;
                ClearInspectorDirectoryUiState(emptyStateVisibility: Visibility.Visible);
                InspectorDirectoryTree.Tag = null;
                InspectorDirectoryRootText.Text = "No active project";
                InspectorDirectoryMetaText.Text = "Open an editor pane to browse files.";
                UpdateInspectorFileActionState();
                return;
            }

            string directoryTitle = _activeProject?.Name;
            if (string.IsNullOrWhiteSpace(directoryTitle))
            {
                directoryTitle = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            InspectorDirectoryRootText.Text = directoryTitle;
            ToolTipService.SetToolTip(InspectorDirectoryRootText, ShellProfiles.ResolveDisplayPath(rootPath, _activeProject?.ShellProfileId ?? SampleConfig.DefaultShellProfileId));
            bool shouldRebuild = forceRebuild ||
                !string.Equals(_lastInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase);
            GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
            IReadOnlyList<GitChangedFile> liveFiles = displayedSnapshot?.ChangedFiles is { } changedFiles
                ? changedFiles
                : Array.Empty<GitChangedFile>();
            string renderKey = BuildInspectorDirectoryRenderKey(liveFiles);
            if (liveFiles.Count > 0 &&
                !string.Equals(InspectorDirectoryTree.Tag as string, renderKey, StringComparison.Ordinal))
            {
                shouldRebuild = true;
            }

            if (shouldRebuild)
            {
                string cacheKey = BuildInspectorDirectoryCacheKey(rootPath, renderKey);
                if (!forceRebuild &&
                    _inspectorDirectoryUiCacheByKey.TryGetValue(cacheKey, out InspectorDirectoryUiCache cachedUi) &&
                    cachedUi.FileCount > 0)
                {
                    ApplyInspectorDirectoryUiCache(cachedUi);
                    shouldRebuild = false;
                }
                else if (!forceRebuild &&
                    TryBuildInspectorDirectoryUiFromRootCache(rootPath, renderKey, liveFiles, out InspectorDirectoryUiCache decoratedUi))
                {
                    CacheInspectorDirectoryUi(decoratedUi);
                    ApplyInspectorDirectoryUiCache(decoratedUi);
                    shouldRebuild = false;
                }
            }

            if (shouldRebuild)
            {
                QueueInspectorDirectoryBuild(
                    rootPath,
                    liveFiles,
                    renderKey,
                    bypassCache: forceRebuild,
                    correlationId: NativeAutomationDiagnostics.CaptureCurrentCorrelationId());
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            string selectedPath = editorPane?.Editor.SelectedFilePath;
            InspectorDirectoryMetaText.Text = editorPane is null
                ? "Select a file to open it in a new editor pane."
                : editorPane.Editor.StatusText;
            UpdateInspectorDirectorySelection(selectedPath);
            UpdateInspectorFileActionState();
        }

        private void CancelPendingInspectorDirectoryBuilds()
        {
            _pendingInspectorDirectoryRootPath = null;
            _pendingInspectorDirectoryRenderKey = null;
            _latestInspectorDirectoryBuildRequestId++;
            _inspectorDirectoryBuildCancellation?.Cancel();
            _inspectorDirectoryBuildCancellation?.Dispose();
            _inspectorDirectoryBuildCancellation = null;
        }

        private void QueueInspectorDirectoryWarmup(
            string rootPath = null,
            IReadOnlyList<GitChangedFile> changedFiles = null,
            string correlationId = null)
        {
            if (_lifetimeResourcesReleased ||
                _showingSettings ||
                _activeThread is null ||
                (_inspectorOpen && _activeInspectorSection == InspectorSection.Files))
            {
                return;
            }

            rootPath ??= ResolveThreadRootPath(_activeProject, _activeThread);
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return;
            }

            changedFiles ??= Array.Empty<GitChangedFile>();
            string renderKey = BuildInspectorDirectoryRenderKey(changedFiles);
            string cacheKey = BuildInspectorDirectoryCacheKey(rootPath, renderKey);
            if ((_inspectorDirectoryUiCacheByKey.TryGetValue(cacheKey, out InspectorDirectoryUiCache exactCache) && exactCache.FileCount > 0) ||
                (string.Equals(_pendingInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_pendingInspectorDirectoryRenderKey, renderKey, StringComparison.Ordinal)) ||
                (string.Equals(_pendingInspectorDirectoryWarmupRootPath, rootPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_pendingInspectorDirectoryWarmupRenderKey, renderKey, StringComparison.Ordinal)))
            {
                return;
            }

            int requestId = ++_latestInspectorDirectoryWarmupRequestId;
            _pendingInspectorDirectoryWarmupRootPath = rootPath;
            _pendingInspectorDirectoryWarmupRenderKey = renderKey;
            GitChangedFile[] snapshotFiles = changedFiles.Count == 0 ? Array.Empty<GitChangedFile>() : changedFiles.ToArray();
            _ = System.Threading.Tasks.Task.Run(
                () => BuildInspectorDirectoryTree(rootPath, snapshotFiles, renderKey, bypassCache: false, correlationId, default))
                .ContinueWith(task =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (requestId != _latestInspectorDirectoryWarmupRequestId)
                        {
                            return;
                        }

                        _pendingInspectorDirectoryWarmupRootPath = null;
                        _pendingInspectorDirectoryWarmupRenderKey = null;
                        if (task.IsCanceled || task.IsFaulted || task.Result is null)
                        {
                            return;
                        }

                        NativeAutomationDiagnostics.IncrementCounter("inspectorDirectory.warmup.count");
                        CacheInspectorDirectoryUi(CreateInspectorDirectoryUiCache(
                            task.Result.RootPath,
                            task.Result.RenderKey,
                            task.Result.FileCount,
                            task.Result.RootNodes));
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private void QueueInspectorDirectoryBuild(
            string rootPath,
            IReadOnlyList<GitChangedFile> changedFiles,
            string renderKey,
            bool bypassCache = false,
            string correlationId = null)
        {
            if (_showingSettings || !_inspectorOpen || _activeThread is null)
            {
                return;
            }

            if (string.Equals(_pendingInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_pendingInspectorDirectoryRenderKey, renderKey, StringComparison.Ordinal) &&
                !bypassCache)
            {
                return;
            }

            _pendingInspectorDirectoryRootPath = rootPath;
            _pendingInspectorDirectoryRenderKey = renderKey;
            int requestId = ++_latestInspectorDirectoryBuildRequestId;
            _inspectorDirectoryBuildCancellation?.Cancel();
            _inspectorDirectoryBuildCancellation?.Dispose();
            _inspectorDirectoryBuildCancellation = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken cancellationToken = _inspectorDirectoryBuildCancellation.Token;

            bool rootChanged = !string.Equals(_lastInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase);
            if (rootChanged)
            {
                ClearInspectorDirectoryUiState(emptyStateVisibility: Visibility.Collapsed);
            }

            _ = System.Threading.Tasks.Task.Run(
                () => BuildInspectorDirectoryTree(rootPath, changedFiles, renderKey, bypassCache, correlationId, cancellationToken),
                cancellationToken)
                .ContinueWith(task =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (requestId != _latestInspectorDirectoryBuildRequestId || task.IsCanceled)
                        {
                            return;
                        }

                        if (task.IsFaulted)
                        {
                            Exception failure = task.Exception?.GetBaseException();
                            LogAutomationEvent("inspector", "directory_build_failed", $"Failed to build project file tree: {failure?.Message ?? "Unknown error"}", new Dictionary<string, string>
                            {
                                ["rootPath"] = rootPath ?? string.Empty,
                                ["renderKey"] = renderKey ?? string.Empty,
                                ["threadId"] = _activeThread?.Id ?? string.Empty,
                                ["projectId"] = _activeProject?.Id ?? string.Empty,
                            });
                            _pendingInspectorDirectoryRootPath = null;
                            _pendingInspectorDirectoryRenderKey = null;
                            _lastInspectorDirectoryRootPath = null;
                            InspectorDirectoryTree.Tag = null;
                            InspectorDirectoryEmptyText.Visibility = Visibility.Visible;
                            UpdateInspectorFileActionState();
                            return;
                        }

                        string activeRootPath = ResolveThreadRootPath(_activeProject, _activeThread);
                        if (!string.Equals(activeRootPath, task.Result.RootPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        ApplyInspectorDirectoryBuildResult(task.Result, correlationId);
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private static InspectorDirectoryBuildResult BuildInspectorDirectoryTree(
            string rootPath,
            IReadOnlyList<GitChangedFile> changedFiles,
            string renderKey,
            bool bypassCache = false,
            string correlationId = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("inspector.build.background", correlationId, background: true, data: new Dictionary<string, string>
            {
                ["rootPath"] = rootPath ?? string.Empty,
            });
            NativeAutomationDiagnostics.IncrementCounter("inspectorFileScan.count");
            IReadOnlyList<EditorPaneFileEntry> files = EditorPaneControl.EnumerateProjectFilesForRoot(rootPath, bypassCache, cancellationToken);
            Dictionary<string, InspectorDirectoryDecoration> decorationsByPath = BuildInspectorDirectoryDecorations(changedFiles);
            Dictionary<string, InspectorDirectoryNodeModel> rootNodes = new(StringComparer.OrdinalIgnoreCase);
            foreach (EditorPaneFileEntry file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] segments = file.RelativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                Dictionary<string, InspectorDirectoryNodeModel> siblings = rootNodes;
                string cumulativePath = string.Empty;

                for (int index = 0; index < segments.Length; index++)
                {
                    string segment = segments[index];
                    cumulativePath = string.IsNullOrWhiteSpace(cumulativePath) ? segment : $"{cumulativePath}/{segment}";
                    bool isFile = index == segments.Length - 1;
                    if (!siblings.TryGetValue(segment, out InspectorDirectoryNodeModel node))
                    {
                        decorationsByPath.TryGetValue(cumulativePath, out InspectorDirectoryDecoration decoration);
                        node = new InspectorDirectoryNodeModel
                        {
                            Name = segment,
                            RelativePath = cumulativePath,
                            IsDirectory = !isFile,
                            Decoration = decoration,
                        };
                        siblings[segment] = node;
                    }

                    if (!isFile)
                    {
                        siblings = node.Children;
                    }
                }
            }

            return new InspectorDirectoryBuildResult
            {
                RootPath = rootPath,
                RenderKey = renderKey,
                FileCount = files.Count,
                RootNodes = OrderInspectorDirectoryNodes(rootNodes.Values).ToList(),
            };
        }

        private static string BuildInspectorDirectoryCacheKey(string rootPath, string renderKey)
        {
            return $"{rootPath ?? string.Empty}|{renderKey ?? string.Empty}";
        }

        private bool TryGetInspectorDirectoryUiForRoot(string rootPath, out InspectorDirectoryUiCache uiCache)
        {
            return _inspectorDirectoryUiCacheByRootPath.TryGetValue(rootPath ?? string.Empty, out uiCache) &&
                uiCache is not null;
        }

        private bool TryBuildInspectorDirectoryUiFromRootCache(
            string rootPath,
            string renderKey,
            IReadOnlyList<GitChangedFile> changedFiles,
            out InspectorDirectoryUiCache uiCache)
        {
            uiCache = null;
            if (!TryGetInspectorDirectoryUiForRoot(rootPath, out InspectorDirectoryUiCache cachedRootUi) ||
                cachedRootUi.FileCount <= 0)
            {
                return false;
            }

            NativeAutomationDiagnostics.IncrementCounter("inspectorDirectory.decorateReuse.count");
            Dictionary<string, InspectorDirectoryDecoration> decorationsByPath = BuildInspectorDirectoryDecorations(changedFiles);
            List<InspectorDirectoryNodeModel> rootNodes = cachedRootUi.RootNodes
                .Select(node => CloneInspectorDirectoryNodeWithDecorations(node, decorationsByPath))
                .ToList();
            uiCache = CreateInspectorDirectoryUiCache(
                cachedRootUi.RootPath,
                renderKey,
                cachedRootUi.FileCount,
                rootNodes);
            return true;
        }

        private void ApplyInspectorDirectoryBuildResult(InspectorDirectoryBuildResult result, string correlationId = null)
        {
            if (_showingSettings || !_inspectorOpen || _activeThread is null || _activeInspectorSection != InspectorSection.Files)
            {
                return;
            }

            using var perfScope = NativeAutomationDiagnostics.TrackOperation("inspector.build.apply", correlationId, background: true, data: new Dictionary<string, string>
            {
                ["rootPath"] = result?.RootPath ?? string.Empty,
            });
            if (result is not null && result.FileCount == 0)
            {
                LogAutomationEvent("inspector", "directory_build_empty", "Project file tree scan returned no editable files", new Dictionary<string, string>
                {
                    ["rootPath"] = result.RootPath ?? string.Empty,
                    ["renderKey"] = result.RenderKey ?? string.Empty,
                    ["threadId"] = _activeThread?.Id ?? string.Empty,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                });
            }

            InspectorDirectoryUiCache uiCache = CreateInspectorDirectoryUiCache(
                result.RootPath,
                result.RenderKey,
                result.FileCount,
                result.RootNodes);
            CacheInspectorDirectoryUi(uiCache);
            ApplyInspectorDirectoryUiCache(uiCache);
        }

        private static InspectorDirectoryUiCache CreateInspectorDirectoryUiCache(
            string rootPath,
            string renderKey,
            int fileCount,
            IReadOnlyList<InspectorDirectoryNodeModel> rootNodes)
        {
            InspectorDirectoryUiCache uiCache = new()
            {
                RootPath = rootPath,
                RenderKey = renderKey,
                FileCount = fileCount,
                RootNodes = rootNodes?.ToList() ?? new List<InspectorDirectoryNodeModel>(),
            };

            foreach (InspectorDirectoryNodeModel node in uiCache.RootNodes)
            {
                IndexInspectorDirectoryNodeModel(node, uiCache.ModelsByPath);
            }

            return uiCache;
        }

        private static InspectorDirectoryNodeModel CloneInspectorDirectoryNodeWithDecorations(
            InspectorDirectoryNodeModel source,
            IReadOnlyDictionary<string, InspectorDirectoryDecoration> decorationsByPath)
        {
            if (source is null)
            {
                return null;
            }

            InspectorDirectoryDecoration decoration = null;
            decorationsByPath?.TryGetValue(source.RelativePath ?? string.Empty, out decoration);
            InspectorDirectoryNodeModel clone = new()
            {
                Name = source.Name,
                RelativePath = source.RelativePath,
                IsDirectory = source.IsDirectory,
                Decoration = decoration,
            };

            foreach ((string name, InspectorDirectoryNodeModel child) in source.Children)
            {
                clone.Children[name] = CloneInspectorDirectoryNodeWithDecorations(child, decorationsByPath);
            }

            return clone;
        }

        private void CacheInspectorDirectoryUi(InspectorDirectoryUiCache uiCache)
        {
            if (uiCache is null)
            {
                return;
            }

            _inspectorDirectoryUiCacheByKey[BuildInspectorDirectoryCacheKey(uiCache.RootPath, uiCache.RenderKey)] = uiCache;
            _inspectorDirectoryUiCacheByRootPath[uiCache.RootPath ?? string.Empty] = uiCache;
            while (_inspectorDirectoryUiCacheByKey.Count > 6)
            {
                string oldestKey = _inspectorDirectoryUiCacheByKey.Keys.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(oldestKey))
                {
                    break;
                }

                if (_inspectorDirectoryUiCacheByKey.TryGetValue(oldestKey, out InspectorDirectoryUiCache evictedUi) &&
                    _inspectorDirectoryUiCacheByRootPath.TryGetValue(evictedUi.RootPath ?? string.Empty, out InspectorDirectoryUiCache cachedRootUi) &&
                    ReferenceEquals(cachedRootUi, evictedUi))
                {
                    _inspectorDirectoryUiCacheByRootPath.Remove(evictedUi.RootPath ?? string.Empty);
                }

                _inspectorDirectoryUiCacheByKey.Remove(oldestKey);
            }
        }

        private void ApplyInspectorDirectoryUiCache(InspectorDirectoryUiCache uiCache)
        {
            _pendingInspectorDirectoryRootPath = null;
            _pendingInspectorDirectoryRenderKey = null;
            ClearInspectorDirectoryUiState();
            InspectorDirectoryTree.Tag = uiCache?.RenderKey;
            InspectorDirectoryEmptyText.Visibility = uiCache is null || uiCache.FileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            _lastInspectorDirectoryRootPath = uiCache?.RootPath;

            if (uiCache is null || uiCache.FileCount == 0)
            {
                UpdateInspectorFileActionState();
                return;
            }

            foreach ((string relativePath, InspectorDirectoryNodeModel model) in uiCache.ModelsByPath)
            {
                _inspectorDirectoryModelsByPath[relativePath] = model;
            }

            foreach (InspectorDirectoryNodeModel rootNode in uiCache.RootNodes)
            {
                InspectorDirectoryTree.RootNodes.Add(BuildInspectorDirectoryTreeNode(rootNode, depth: 0));
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            UpdateInspectorDirectorySelection(editorPane?.Editor.SelectedFilePath);
            UpdateInspectorFileActionState();
        }

        private void ClearInspectorDirectoryUiState(Visibility emptyStateVisibility = Visibility.Collapsed)
        {
            _inspectorDirectoryNodesByPath.Clear();
            _inspectorDirectoryItemsByNode.Clear();
            _inspectorDirectoryModelsByNode.Clear();
            _inspectorDirectoryDepthByNode.Clear();
            _inspectorDirectoryModelsByPath.Clear();
            InspectorDirectoryTree.SelectedNode = null;
            InspectorDirectoryTree.RootNodes.Clear();
            InspectorDirectoryEmptyText.Visibility = emptyStateVisibility;
        }

        private static void IndexInspectorDirectoryNodeModel(
            InspectorDirectoryNodeModel node,
            Dictionary<string, InspectorDirectoryNodeModel> modelsByPath)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.RelativePath))
            {
                return;
            }

            modelsByPath[node.RelativePath] = node;
            if (!node.IsDirectory)
            {
                return;
            }

            foreach (InspectorDirectoryNodeModel child in node.Children.Values)
            {
                IndexInspectorDirectoryNodeModel(child, modelsByPath);
            }
        }

        private TreeViewNode BuildInspectorDirectoryTreeNode(InspectorDirectoryNodeModel node, int depth)
        {
            InspectorDirectoryDecoration decoration = node.Decoration;
            FileIconInfo themeIcon = FileIconTheme.Resolve(node.RelativePath, node.IsDirectory);
            Brush defaultIconBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorIconBrushKey(node.RelativePath, node.IsDirectory, decoration));
            Brush resolvedIconBrush = node.IsDirectory && decoration?.HasChangedDescendant == true
                ? defaultIconBrush
                : themeIcon?.Brush ?? defaultIconBrush;
            InspectorDirectoryTreeItem item = new()
            {
                Name = node.Name,
                RelativePath = node.RelativePath,
                IsDirectory = node.IsDirectory,
                IconGlyph = themeIcon?.Glyph ?? ResolveInspectorItemGlyph(node.RelativePath, node.IsDirectory),
                IconFontFamily = themeIcon?.FontFamily,
                IconFontSize = themeIcon?.FontSize ?? 11,
                UseGlyphBadge = node.IsDirectory || themeIcon is not null,
                IconBrush = resolvedIconBrush,
                KindText = themeIcon is null ? ResolveInspectorKindText(node.RelativePath, node.IsDirectory) : string.Empty,
                KindBrush = resolvedIconBrush,
                StatusText = ResolveInspectorChangeMarker(decoration?.File, hasChangedDescendant: decoration?.HasChangedDescendant == true),
                StatusBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorChangeBrushKey(decoration?.File, hasChangedDescendant: decoration?.HasChangedDescendant == true)),
            };

            TreeViewNode treeNode = new()
            {
                Content = BuildInspectorDirectoryNodeContent(item),
                IsExpanded = node.IsDirectory && ShouldExpandInspectorDirectoryNode(node, depth),
            };

            _inspectorDirectoryItemsByNode[treeNode] = item;
            _inspectorDirectoryModelsByNode[treeNode] = node;
            _inspectorDirectoryDepthByNode[treeNode] = depth;
            _inspectorDirectoryNodesByPath[item.RelativePath] = treeNode;
            if (node.IsDirectory && node.Children.Count > 0)
            {
                treeNode.HasUnrealizedChildren = true;
                if (treeNode.IsExpanded)
                {
                    MaterializeInspectorDirectoryChildren(treeNode);
                }
            }

            return treeNode;
        }

        private void MaterializeInspectorDirectoryChildren(TreeViewNode node)
        {
            if (node is null ||
                !node.HasUnrealizedChildren ||
                !_inspectorDirectoryModelsByNode.TryGetValue(node, out InspectorDirectoryNodeModel model) ||
                !model.IsDirectory)
            {
                return;
            }

            int childDepth = (_inspectorDirectoryDepthByNode.TryGetValue(node, out int depth) ? depth : 0) + 1;
            foreach (InspectorDirectoryNodeModel child in OrderInspectorDirectoryNodes(model.Children.Values))
            {
                node.Children.Add(BuildInspectorDirectoryTreeNode(child, childDepth));
            }

            node.HasUnrealizedChildren = false;
        }

        private static IEnumerable<InspectorDirectoryNodeModel> OrderInspectorDirectoryNodes(IEnumerable<InspectorDirectoryNodeModel> nodes)
        {
            return nodes
                .OrderByDescending(node => node.IsDirectory)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildInspectorDirectoryRenderKey(IReadOnlyList<GitChangedFile> changedFiles)
        {
            if (changedFiles is null || changedFiles.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            foreach (GitChangedFile file in changedFiles)
            {
                if (file is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('|');
                }

                builder
                    .Append(file.Path ?? string.Empty)
                    .Append(':')
                    .Append(file.Status);
            }

            return builder.ToString();
        }

        private static bool ShouldExpandInspectorDirectoryNode(InspectorDirectoryNodeModel node, int depth)
        {
            return depth == 0 && node.Decoration?.HasChangedDescendant == true;
        }

        private InspectorDirectoryTreeItem ResolveSelectedInspectorDirectoryItem()
        {
            return InspectorDirectoryTree?.SelectedNode is TreeViewNode node && _inspectorDirectoryItemsByNode.TryGetValue(node, out InspectorDirectoryTreeItem item)
                ? item
                : null;
        }

        private FrameworkElement BuildInspectorDirectoryNodeContent(InspectorDirectoryTreeItem item)
        {
            Grid row = new()
            {
                MinHeight = 24,
                ColumnSpacing = 6,
                Margin = new Thickness(-6, 1, 0, 1),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ToolTipService.SetToolTip(row, item.RelativePath);

            Brush accentBrush = !string.IsNullOrWhiteSpace(item.StatusText)
                ? item.StatusBrush
                : item.IconBrush ?? item.KindBrush ?? AppBrush(InspectorDirectoryTree, "ShellPaneActiveBorderBrush");

            Border accent = new()
            {
                Width = 2,
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(999),
                Background = accentBrush,
                Opacity = item.IsDirectory ? 0.45 : 0.82,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            row.Children.Add(accent);

            FrameworkElement glyph = BuildInspectorNodeGlyph(item);
            Grid.SetColumn(glyph, 1);
            row.Children.Add(glyph);

            TextBlock name = new()
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.1,
                FontWeight = item.IsDirectory
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = AppBrush(InspectorDirectoryTree, "ShellTextPrimaryBrush"),
                Text = item.Name,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 2);
            row.Children.Add(name);

            StackPanel adornments = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(adornments, 3);

            if (!string.IsNullOrWhiteSpace(item.StatusText))
            {
                adornments.Children.Add(BuildInspectorChangeBadge(item));
            }

            if (adornments.Children.Count > 0)
            {
                row.Children.Add(adornments);
            }

            return row;
        }

        private FrameworkElement BuildInspectorNodeGlyph(InspectorDirectoryTreeItem item)
        {
            return BuildInspectorPathBadge(item);
        }

        private FrameworkElement BuildInspectorChangeBadge(InspectorDirectoryTreeItem item)
        {
            if (item.IsDirectory)
            {
                return new Border
                {
                    Width = 4,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = item.StatusBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return new TextBlock
            {
                Text = item.StatusText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.StatusBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.9,
            };
        }

        private static Brush CreateInspectorBadgeBackground(Brush foreground, bool isDirectory)
        {
            Windows.UI.Color fallback = isDirectory
                ? Windows.UI.Color.FromArgb(0x22, 0x71, 0x71, 0x7A)
                : Windows.UI.Color.FromArgb(0x18, 0x71, 0x71, 0x7A);
            if (foreground is not SolidColorBrush solid)
            {
                return new SolidColorBrush(fallback);
            }

            Windows.UI.Color color = solid.Color;
            byte alpha = isDirectory ? (byte)0x24 : (byte)0x16;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        private static Brush CreateInspectorStatusBackground(Brush foreground, bool isDirectory)
        {
            Windows.UI.Color fallback = isDirectory
                ? Windows.UI.Color.FromArgb(0x20, 0x71, 0x71, 0x7A)
                : Windows.UI.Color.FromArgb(0x28, 0x71, 0x71, 0x7A);
            if (foreground is not SolidColorBrush solid)
            {
                return new SolidColorBrush(fallback);
            }

            Windows.UI.Color color = solid.Color;
            byte alpha = isDirectory ? (byte)0x20 : (byte)0x28;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        private static Dictionary<string, InspectorDirectoryDecoration> BuildInspectorDirectoryDecorations(IReadOnlyList<GitChangedFile> changedFiles)
        {
            Dictionary<string, InspectorDirectoryDecoration> map = new(StringComparer.OrdinalIgnoreCase);
            foreach (GitChangedFile file in changedFiles ?? Array.Empty<GitChangedFile>())
            {
                if (string.IsNullOrWhiteSpace(file?.Path))
                {
                    continue;
                }

                string normalizedPath = file.Path.Replace('\\', '/');
                map[normalizedPath] = new InspectorDirectoryDecoration
                {
                    File = file,
                };

                int slashIndex = normalizedPath.LastIndexOf('/');
                while (slashIndex > 0)
                {
                    string directoryPath = normalizedPath[..slashIndex];
                    map[directoryPath] = new InspectorDirectoryDecoration
                    {
                        File = map.TryGetValue(directoryPath, out InspectorDirectoryDecoration existing) ? existing.File : null,
                        HasChangedDescendant = true,
                    };
                    slashIndex = directoryPath.LastIndexOf('/');
                }
            }

            return map;
        }

        private static string ResolveInspectorItemGlyph(string relativePath, bool isDirectory)
        {
            return isDirectory ? "\uE8B7" : "\uE8A5";
        }

        private static string ResolveInspectorKindText(string relativePath, bool isDirectory)
        {
            if (isDirectory)
            {
                return string.Empty;
            }

            string extension = Path.GetExtension(relativePath ?? string.Empty)?.ToLowerInvariant();
            return extension switch
            {
                ".appxmanifest" or ".manifest" => "APPX",
                ".csproj" or ".props" or ".targets" => "PROJ",
                ".cs" => "C#",
                ".css" => "CSS",
                ".html" => "HTML",
                ".ini" => "INI",
                ".js" => "JS",
                ".mjs" => "MJS",
                ".json" => "JSON",
                ".jsx" => "JSX",
                ".md" => "MD",
                ".ps1" => "PS1",
                ".sh" => "SH",
                ".cmd" or ".bat" => "CMD",
                ".toml" => "TOML",
                ".ts" => "TS",
                ".tsx" => "TSX",
                ".txt" => "TXT",
                ".xaml" => "XAML",
                ".xml" => "XML",
                ".yaml" or ".yml" => "YAML",
                ".resw" => "RESW",
                ".sln" => "SLN",
                _ => string.IsNullOrWhiteSpace(extension)
                    ? "FILE"
                    : extension.TrimStart('.').ToUpperInvariant()[..Math.Min(extension.Length - 1, 4)],
            };
        }

        private static string ResolveInspectorIconBrushKey(string relativePath, bool isDirectory, InspectorDirectoryDecoration decoration)
        {
            if (isDirectory)
            {
                return decoration?.HasChangedDescendant == true
                    ? "ShellWarningBrush"
                    : "ShellTextTertiaryBrush";
            }

            string extension = Path.GetExtension(relativePath ?? string.Empty)?.ToLowerInvariant();
            return extension switch
            {
                ".cs" or ".csproj" or ".props" or ".targets" => "ShellCSharpBrush",
                ".ts" or ".tsx" => "ShellTypeScriptBrush",
                ".js" or ".jsx" or ".mjs" => "ShellJavaScriptBrush",
                ".ps1" or ".sh" or ".cmd" or ".bat" => "ShellScriptBrush",
                ".json" or ".yml" or ".yaml" or ".toml" or ".ini" => "ShellConfigBrush",
                ".md" or ".txt" => "ShellMarkdownBrush",
                ".xaml" or ".xml" or ".html" => "ShellMarkupBrush",
                ".css" => "ShellStyleBrush",
                _ => "ShellTextTertiaryBrush",
            };
        }

        private static FrameworkElement BuildInspectorPathBadge(InspectorDirectoryTreeItem item)
        {
            if (item.UseGlyphBadge)
            {
                return new FontIcon
                {
                    Glyph = item.IconGlyph,
                    FontFamily = item.IconFontFamily,
                    FontSize = item.IconFontSize,
                    Foreground = item.IconBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return new TextBlock
            {
                Text = item.KindText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = item.KindText?.Length > 3 ? 8.8 : 9.4,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.KindBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static FrameworkElement BuildInspectorPathBadge(string relativePath, bool isDirectory, Brush accentBrush)
        {
            InspectorDirectoryTreeItem item = new()
            {
                RelativePath = relativePath,
                IsDirectory = isDirectory,
                IconGlyph = ResolveInspectorItemGlyph(relativePath, isDirectory),
                IconBrush = accentBrush,
                KindText = ResolveInspectorKindText(relativePath, isDirectory),
                KindBrush = accentBrush,
                UseGlyphBadge = isDirectory,
            };

            if (item.UseGlyphBadge)
            {
                return new FontIcon
                {
                    Glyph = item.IconGlyph,
                    FontSize = 11,
                    Foreground = item.IconBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return new TextBlock
            {
                Text = item.KindText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = item.KindText?.Length > 3 ? 8.8 : 9.4,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.KindBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static string ResolveInspectorChangeMarker(GitChangedFile file, bool hasChangedDescendant)
        {
            if (file is null)
            {
                return hasChangedDescendant ? "•" : string.Empty;
            }

            string status = file.Status?.Trim() ?? string.Empty;
            if (status == "??" || status.IndexOf('A') >= 0)
            {
                return "A";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "D";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "R";
            }

            return "M";
        }

        private static string ResolveInspectorChangeBrushKey(GitChangedFile file, bool hasChangedDescendant)
        {
            if (file is null)
            {
                return hasChangedDescendant ? "ShellWarningBrush" : "ShellTextTertiaryBrush";
            }

            string status = file.Status?.Trim() ?? string.Empty;
            if (status == "??" || status.IndexOf('A') >= 0)
            {
                return "ShellSuccessBrush";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "ShellDangerBrush";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "ShellInfoBrush";
            }

            return "ShellWarningBrush";
        }

        private void UpdateInspectorDirectorySelection(string selectedPath)
        {
            if (InspectorDirectoryTree is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedPath) ||
                !TryEnsureInspectorDirectoryNode(selectedPath, out TreeViewNode node))
            {
                if (InspectorDirectoryTree.SelectedNode is not null)
                {
                    InspectorDirectoryTree.SelectedNode = null;
                }

                return;
            }

            if (ReferenceEquals(InspectorDirectoryTree.SelectedNode, node))
            {
                return;
            }

            TreeViewNode current = node.Parent as TreeViewNode;
            while (current is not null)
            {
                MaterializeInspectorDirectoryChildren(current);
                current.IsExpanded = true;
                current = current.Parent as TreeViewNode;
            }

            InspectorDirectoryTree.SelectedNode = node;
        }

        private bool TryEnsureInspectorDirectoryNode(string relativePath, out TreeViewNode node)
        {
            if (_inspectorDirectoryNodesByPath.TryGetValue(relativePath, out node))
            {
                return true;
            }

            if (!_inspectorDirectoryModelsByPath.TryGetValue(relativePath, out InspectorDirectoryNodeModel model))
            {
                node = null;
                return false;
            }

            string parentPath = GetInspectorDirectoryParentPath(model.RelativePath);
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                if (!TryEnsureInspectorDirectoryNode(parentPath, out TreeViewNode parentNode))
                {
                    node = null;
                    return false;
                }

                MaterializeInspectorDirectoryChildren(parentNode);
                parentNode.IsExpanded = true;
            }

            return _inspectorDirectoryNodesByPath.TryGetValue(relativePath, out node);
        }

        private static string GetInspectorDirectoryParentPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            int separatorIndex = relativePath.LastIndexOf('/');
            return separatorIndex > 0 ? relativePath[..separatorIndex] : null;
        }

        private void UpdateInspectorFileActionState()
        {
            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            WorkspaceThreadNote selectedNote = ResolveSelectedThreadNote(_activeThread);
            bool canSaveSelectedNote = selectedNote is { IsArchived: false } &&
                ResolveNoteDraftState(selectedNote)?.Dirty == true;
            if (InspectorCollapseAllButton is not null)
            {
                InspectorCollapseAllButton.IsEnabled = InspectorDirectoryTree?.RootNodes.Count > 0;
            }

            if (InspectorSaveFileButton is not null)
            {
                InspectorSaveFileButton.IsEnabled = editorPane?.Editor.CanSave == true;
            }

            if (InspectorAddNoteButton is not null)
            {
                InspectorAddNoteButton.IsEnabled = _activeThread is not null;
            }

            if (InspectorSaveNoteButton is not null)
            {
                InspectorSaveNoteButton.IsEnabled = canSaveSelectedNote;
            }

            if (InspectorDeleteNoteButton is not null)
            {
                InspectorDeleteNoteButton.IsEnabled = selectedNote is not null;
            }

            if (InspectorInlineAddNoteButton is not null)
            {
                InspectorInlineAddNoteButton.IsEnabled = _activeThread is not null;
            }

            if (InspectorInlineSaveNoteButton is not null)
            {
                InspectorInlineSaveNoteButton.IsEnabled = canSaveSelectedNote;
            }

            if (InspectorInlineDeleteNoteButton is not null)
            {
                InspectorInlineDeleteNoteButton.IsEnabled = selectedNote is not null;
            }
        }

        private static void CollapseInspectorDirectoryNode(TreeViewNode node)
        {
            if (node is null)
            {
                return;
            }

            node.IsExpanded = false;
            foreach (TreeViewNode childNode in node.Children)
            {
                CollapseInspectorDirectoryNode(childNode);
            }
        }
    }
}
