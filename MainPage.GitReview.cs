using Microsoft.UI.Xaml;
using SelfContainedDeployment.Git;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SelfContainedDeployment
{
    public partial class MainPage
    {
        private bool RequiresActiveThreadGitRefresh(WorkspaceThread thread, string selectedPath, bool includeSelectedDiff, bool preferFastRefresh)
        {
            if (thread is null)
            {
                return true;
            }

            string threadRootPath = ResolveThreadRootPath(thread.Project, thread);
            bool requiresCompleteSnapshot = !preferFastRefresh && VisibleDiffPaneRequiresCompleteSnapshot(thread);
            TimeSpan maxAge = ResolveGitSnapshotMaxAge(includeSelectedDiff, preferFastRefresh, requiresCompleteSnapshot);
            return !SnapshotSatisfiesGitRefreshNeeds(
                thread.LiveSnapshot,
                thread.LiveSnapshotCapturedAt,
                threadRootPath,
                selectedPath,
                includeSelectedDiff,
                requiresCompleteSnapshot,
                maxAge);
        }

        private bool VisibleDiffPaneRequiresCompleteSnapshot(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return false;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                if (pane is DiffPaneRecord diffPane &&
                    diffPane.DiffPane.DisplayMode == DiffPaneDisplayMode.FullPatchReview)
                {
                    return true;
                }
            }

            string fallbackPath = ReferenceEquals(thread, _activeThread)
                ? _activeGitSnapshot?.SelectedPath ?? thread.SelectedDiffPath
                : thread.SelectedDiffPath;
            List<string> visibleDiffPaths = GetVisibleFileCompareDiffPaths(thread, fallbackPath);
            return visibleDiffPaths.Count > 1 ||
                (visibleDiffPaths.Count == 1 &&
                    !string.Equals(visibleDiffPaths[0], fallbackPath, StringComparison.Ordinal));
        }

        private bool VisibleDiffPaneNeedsSelectedDiff(WorkspaceThread thread, string selectedPath)
        {
            if (thread is null ||
                thread.DiffReviewSource != DiffReviewSourceKind.Live ||
                VisibleDiffPaneRequiresCompleteSnapshot(thread))
            {
                return false;
            }

            string fallbackPath = string.IsNullOrWhiteSpace(selectedPath)
                ? (ReferenceEquals(thread, _activeThread)
                    ? _activeGitSnapshot?.SelectedPath ?? thread.SelectedDiffPath
                    : thread.SelectedDiffPath)
                : selectedPath;
            List<string> visibleDiffPaths = GetVisibleFileCompareDiffPaths(thread, fallbackPath);
            return visibleDiffPaths.Count == 1 &&
                string.Equals(visibleDiffPaths[0], fallbackPath, StringComparison.Ordinal);
        }

        private static string ResolveVisibleDiffPanePath(DiffPaneRecord diffPane, string fallbackPath)
        {
            return string.IsNullOrWhiteSpace(diffPane?.DiffPath)
                ? fallbackPath
                : diffPane.DiffPath;
        }

        private List<string> GetVisibleFileCompareDiffPaths(WorkspaceThread thread, string fallbackPath)
        {
            if (thread is null)
            {
                return new List<string>();
            }

            List<string> paths = new();
            HashSet<string> uniquePaths = new(StringComparer.Ordinal);
            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                if (pane is not DiffPaneRecord diffPane ||
                    diffPane.DiffPane.DisplayMode != DiffPaneDisplayMode.FileCompare)
                {
                    continue;
                }

                string path = ResolveVisibleDiffPanePath(diffPane, fallbackPath);
                if (!string.IsNullOrWhiteSpace(path) && uniquePaths.Add(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private bool SnapshotSatisfiesGitRefreshNeeds(
            GitThreadSnapshot snapshot,
            DateTimeOffset capturedAt,
            string threadRootPath,
            string selectedPath,
            bool includeSelectedDiff,
            bool requiresCompleteSnapshot,
            TimeSpan maxAge)
        {
            if (snapshot is null)
            {
                return false;
            }

            if (!string.Equals(snapshot.WorktreePath, threadRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (capturedAt == default || DateTimeOffset.UtcNow - capturedAt > maxAge)
            {
                return false;
            }

            if (requiresCompleteSnapshot && !HasCompleteDiffSet(snapshot))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(selectedPath) && snapshot.ChangedFiles.Count > 0)
            {
                bool hasSelectedPath = false;
                foreach (GitChangedFile file in snapshot.ChangedFiles)
                {
                    if (string.Equals(file.Path, selectedPath, StringComparison.Ordinal))
                    {
                        hasSelectedPath = true;
                        break;
                    }
                }

                if (!hasSelectedPath)
                {
                    return false;
                }
            }

            if (includeSelectedDiff && !HasSelectedDiffAvailable(snapshot, selectedPath))
            {
                return false;
            }

            return true;
        }

        private static TimeSpan ResolveGitSnapshotMaxAge(bool includeSelectedDiff, bool preferFastRefresh, bool requiresCompleteSnapshot)
        {
            if (preferFastRefresh && !includeSelectedDiff && !requiresCompleteSnapshot)
            {
                return CachedShellOnlyGitSnapshotMaxAge;
            }

            return CachedThreadGitSnapshotMaxAge;
        }

        private bool TryUsePeerThreadGitSnapshot(WorkspaceThread thread, string selectedPath, bool includeSelectedDiff, bool preferFastRefresh)
        {
            if (thread is null)
            {
                return false;
            }

            string threadRootPath = ResolveThreadRootPath(thread.Project, thread);
            if (string.IsNullOrWhiteSpace(threadRootPath))
            {
                return false;
            }

            bool requiresCompleteSnapshot = !preferFastRefresh && VisibleDiffPaneRequiresCompleteSnapshot(thread);
            TimeSpan maxAge = ResolveGitSnapshotMaxAge(includeSelectedDiff, preferFastRefresh, requiresCompleteSnapshot);
            WorkspaceThread newestThread = null;
            DateTimeOffset newestCapturedAt = default;

            foreach (WorkspaceThread candidate in _projects.SelectMany(project => project.Threads))
            {
                if (ReferenceEquals(candidate, thread) ||
                    !SnapshotSatisfiesGitRefreshNeeds(
                        candidate.LiveSnapshot,
                        candidate.LiveSnapshotCapturedAt,
                        threadRootPath,
                        selectedPath,
                        includeSelectedDiff,
                        requiresCompleteSnapshot,
                        maxAge))
                {
                    continue;
                }

                if (newestThread is null || candidate.LiveSnapshotCapturedAt > newestCapturedAt)
                {
                    newestThread = candidate;
                    newestCapturedAt = candidate.LiveSnapshotCapturedAt;
                }
            }

            if (newestThread?.LiveSnapshot is null)
            {
                return false;
            }

            GitThreadSnapshot adoptedSnapshot = GitStatusService.CloneSnapshot(newestThread.LiveSnapshot);
            MergeCachedDiffTexts(thread.LiveSnapshot, adoptedSnapshot);
            GitStatusService.SelectDiffPath(adoptedSnapshot, selectedPath);
            CommitActiveGitSnapshot(adoptedSnapshot, newestCapturedAt, ensureBaselineCapture: false, logRefresh: false, updateHeader: true);
            return true;
        }

        private static void SetThreadLiveSnapshot(WorkspaceThread thread, GitThreadSnapshot snapshot, DateTimeOffset capturedAt)
        {
            if (thread is null)
            {
                return;
            }

            if (snapshot is null)
            {
                thread.LiveSnapshot = null;
                thread.LiveSnapshotCapturedAt = capturedAt;
                thread.ChangedFileCount = 0;
                thread.SelectedDiffPath = null;
                return;
            }

            thread.BranchName = snapshot.BranchName;
            thread.WorktreePath = string.IsNullOrWhiteSpace(thread.WorktreePath)
                ? snapshot.WorktreePath
                : thread.WorktreePath;
            thread.ChangedFileCount = snapshot.ChangedFiles.Count;
            thread.SelectedDiffPath = snapshot.SelectedPath;
            thread.LiveSnapshot = snapshot;
            thread.LiveSnapshotCapturedAt = capturedAt;
        }

        private void CommitActiveGitSnapshot(
            GitThreadSnapshot snapshot,
            DateTimeOffset capturedAt,
            bool ensureBaselineCapture,
            bool logRefresh,
            bool updateHeader = false)
        {
            if (_activeThread is null || snapshot is null)
            {
                return;
            }

            MergeCachedDiffTexts(_activeGitSnapshot, snapshot);
            _activeGitSnapshot = snapshot;
            SetThreadLiveSnapshot(_activeThread, snapshot, capturedAt);
            if (ensureBaselineCapture && EnableAutomaticBaselineCapture)
            {
                EnsureThreadBaselineCapture(_activeThread, _activeProject, snapshot);
            }
            ApplyGitSnapshotToUi();
            QueueProjectTreeRefresh();

            if (updateHeader)
            {
                UpdateHeader();
            }

            if (!logRefresh)
            {
                return;
            }

            LogAutomationEvent("git", "thread.snapshot_refreshed", string.IsNullOrWhiteSpace(snapshot.Error) ? "Refreshed thread git snapshot" : snapshot.Error, new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["branch"] = snapshot.BranchName ?? string.Empty,
                ["worktreePath"] = snapshot.WorktreePath ?? string.Empty,
                ["changedFileCount"] = snapshot.ChangedFiles.Count.ToString(),
                ["selectedPath"] = snapshot.SelectedPath ?? string.Empty,
            });
        }

        private void QueueVisibleDiffHydrationIfNeeded(
            WorkspaceThread thread,
            WorkspaceProject project,
            GitThreadSnapshot snapshot = null)
        {
            if (thread is null ||
                project is null ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject) ||
                _showingSettings ||
                thread.DiffReviewSource != DiffReviewSourceKind.Live)
            {
                return;
            }

            GitThreadSnapshot candidateSnapshot = snapshot ?? _activeGitSnapshot ?? thread.LiveSnapshot;
            bool requiresCompleteSnapshot = VisibleDiffPaneRequiresCompleteSnapshot(thread);
            string fallbackPath = candidateSnapshot?.SelectedPath ?? thread.SelectedDiffPath;
            List<string> visibleDiffPaths = GetVisibleFileCompareDiffPaths(thread, fallbackPath);
            string hydrationPath = !string.IsNullOrWhiteSpace(fallbackPath)
                ? fallbackPath
                : visibleDiffPaths.FirstOrDefault();
            if (!requiresCompleteSnapshot &&
                (visibleDiffPaths.Count == 0 || HasSelectedDiffAvailable(candidateSnapshot, hydrationPath)))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() => _ = RefreshActiveThreadGitStateAsync(
                hydrationPath,
                preserveSelection: true,
                includeSelectedDiff: !requiresCompleteSnapshot,
                preferFastRefresh: false));
        }

        private GitThreadSnapshot ResolveDisplayedGitSnapshot()
        {
            if (_activeThread is null)
            {
                return null;
            }

            NormalizeDiffReviewSource(_activeThread);
            return _activeThread.DiffReviewSource switch
            {
                DiffReviewSourceKind.Baseline when _activeThread.BaselineSnapshot is not null => _activeThread.BaselineSnapshot,
                DiffReviewSourceKind.Checkpoint when !string.IsNullOrWhiteSpace(_activeThread.SelectedCheckpointId) =>
                    _activeThread.DiffCheckpoints.FirstOrDefault(checkpoint => string.Equals(checkpoint.Id, _activeThread.SelectedCheckpointId, StringComparison.Ordinal))?.Snapshot
                    ?? _activeGitSnapshot,
                _ => _activeGitSnapshot ?? _activeThread.LiveSnapshot,
            };
        }

        private IReadOnlyList<DiffReviewSourceOption> BuildDiffReviewSourceOptions(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return Array.Empty<DiffReviewSourceOption>();
            }

            List<DiffReviewSourceOption> options = new()
            {
                new DiffReviewSourceOption
                {
                    Kind = DiffReviewSourceKind.Live,
                    Label = "Live working tree",
                }
            };

            if (thread.BaselineSnapshot is not null)
            {
                options.Add(new DiffReviewSourceOption
                {
                    Kind = DiffReviewSourceKind.Baseline,
                    Label = "Thread baseline",
                });
            }

            foreach (WorkspaceDiffCheckpoint checkpoint in thread.DiffCheckpoints.OrderByDescending(candidate => candidate.CapturedAt))
            {
                options.Add(new DiffReviewSourceOption
                {
                    Kind = DiffReviewSourceKind.Checkpoint,
                    CheckpointId = checkpoint.Id,
                    Label = string.IsNullOrWhiteSpace(checkpoint.Name)
                        ? checkpoint.CapturedAt.LocalDateTime.ToString("MMM d HH:mm")
                        : checkpoint.Name,
                });
            }

            return options;
        }

        private void RefreshDiffReviewSourceControls()
        {
            if (DiffReviewSourceComboBox is null || DiffReviewSourceMetaText is null || CaptureCheckpointButton is null || DiffReviewSourceSection is null)
            {
                return;
            }

            if (_showingSettings || !_inspectorOpen)
            {
                DiffReviewSourceSection.Visibility = Visibility.Collapsed;
                return;
            }

            if (_activeThread is null)
            {
                DiffReviewSourceSection.Visibility = Visibility.Collapsed;
                _suppressDiffReviewSourceSelectionChanged = true;
                DiffReviewSourceComboBox.ItemsSource = null;
                DiffReviewSourceComboBox.SelectedItem = null;
                _suppressDiffReviewSourceSelectionChanged = false;
                DiffReviewSourceComboBox.IsEnabled = false;
                DiffReviewSourceMetaText.Text = "No thread selected";
                CaptureCheckpointButton.IsEnabled = false;
                return;
            }

            IReadOnlyList<DiffReviewSourceOption> options = BuildDiffReviewSourceOptions(_activeThread);
            DiffReviewSourceOption selectedOption = options.FirstOrDefault(option =>
                option.Kind == _activeThread.DiffReviewSource &&
                string.Equals(option.CheckpointId, _activeThread.SelectedCheckpointId, StringComparison.Ordinal))
                ?? options.FirstOrDefault();

            _suppressDiffReviewSourceSelectionChanged = true;
            DiffReviewSourceComboBox.DisplayMemberPath = nameof(DiffReviewSourceOption.Label);
            DiffReviewSourceComboBox.ItemsSource = options;
            DiffReviewSourceComboBox.SelectedItem = selectedOption;
            _suppressDiffReviewSourceSelectionChanged = false;

            DiffReviewSourceSection.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            DiffReviewSourceComboBox.IsEnabled = options.Count > 1;
            CaptureCheckpointButton.IsEnabled = !_capturingDiffCheckpoint;
            DiffReviewSourceMetaText.Text = BuildDiffReviewSourceMeta(_activeThread, selectedOption);
        }

        private static string BuildDiffReviewSourceMeta(WorkspaceThread thread, DiffReviewSourceOption selectedOption)
        {
            if (thread is null || selectedOption is null)
            {
                return "Live working tree";
            }

            return selectedOption.Kind switch
            {
                DiffReviewSourceKind.Live => thread.BaselineSnapshot is null
                    ? "Current working tree"
                    : $"Current working tree · {thread.DiffCheckpoints.Count} checkpoint{(thread.DiffCheckpoints.Count == 1 ? string.Empty : "s")}",
                DiffReviewSourceKind.Baseline => "Thread-start snapshot",
                DiffReviewSourceKind.Checkpoint => thread.DiffCheckpoints.FirstOrDefault(checkpoint => string.Equals(checkpoint.Id, selectedOption.CheckpointId, StringComparison.Ordinal)) is WorkspaceDiffCheckpoint checkpoint
                    ? $"Checkpoint · {checkpoint.CapturedAt.LocalDateTime:g}"
                    : "Saved checkpoint",
                _ => "Current working tree",
            };
        }

        private void ApplyDiffReviewSourceSelection(DiffReviewSourceKind kind, string checkpointId = null)
        {
            if (_activeThread is null)
            {
                return;
            }

            _activeThread.DiffReviewSource = kind;
            _activeThread.SelectedCheckpointId = kind == DiffReviewSourceKind.Checkpoint ? checkpointId : null;
            NormalizeDiffReviewSource(_activeThread);
            RefreshDiffReviewSourceControls();
            ApplyGitSnapshotToUi();
            QueueSessionSave();
            LogAutomationEvent("git", "review_source.selected", $"Selected {FormatDiffReviewSource(_activeThread.DiffReviewSource)} review source", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["reviewSource"] = FormatDiffReviewSource(_activeThread.DiffReviewSource),
                ["checkpointId"] = _activeThread.SelectedCheckpointId ?? string.Empty,
            });
        }

        private void SelectDiffReviewSource(string value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "live", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDiffReviewSourceSelection(DiffReviewSourceKind.Live);
                return;
            }

            if (string.Equals(normalized, "baseline", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDiffReviewSourceSelection(DiffReviewSourceKind.Baseline);
                return;
            }

            string checkpointId = normalized.StartsWith("checkpoint:", StringComparison.OrdinalIgnoreCase)
                ? normalized["checkpoint:".Length..]
                : normalized;
            ApplyDiffReviewSourceSelection(DiffReviewSourceKind.Checkpoint, checkpointId);
        }

        private static string FormatDiffReviewSource(DiffReviewSourceKind kind)
        {
            return kind switch
            {
                DiffReviewSourceKind.Baseline => "baseline",
                DiffReviewSourceKind.Checkpoint => "checkpoint",
                _ => "live",
            };
        }

        private static DiffReviewSourceKind ParseDiffReviewSource(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "baseline" => DiffReviewSourceKind.Baseline,
                "checkpoint" => DiffReviewSourceKind.Checkpoint,
                _ => DiffReviewSourceKind.Live,
            };
        }

        private static void NormalizeDiffReviewSource(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return;
            }

            if (thread.DiffReviewSource == DiffReviewSourceKind.Baseline && thread.BaselineSnapshot is not null)
            {
                return;
            }

            if (thread.DiffReviewSource == DiffReviewSourceKind.Checkpoint &&
                !string.IsNullOrWhiteSpace(thread.SelectedCheckpointId) &&
                thread.DiffCheckpoints.Any(checkpoint => string.Equals(checkpoint.Id, thread.SelectedCheckpointId, StringComparison.Ordinal)))
            {
                return;
            }

            thread.DiffReviewSource = DiffReviewSourceKind.Live;
            thread.SelectedCheckpointId = null;
        }

        private async System.Threading.Tasks.Task CaptureDiffCheckpointAsync(string checkpointName = null)
        {
            if (_activeThread is null || _activeProject is null || _capturingDiffCheckpoint)
            {
                return;
            }

            WorkspaceThread thread = _activeThread;
            WorkspaceProject project = _activeProject;
            string selectedPath = ResolveDisplayedGitSnapshot()?.SelectedPath ?? thread.SelectedDiffPath;
            string worktreePath = thread.WorktreePath ?? project.RootPath;
            _capturingDiffCheckpoint = true;
            RefreshDiffReviewSourceControls();

            try
            {
                GitThreadSnapshot checkpointSnapshot = await System.Threading.Tasks.Task
                    .Run(() => GitStatusService.CaptureComplete(worktreePath, selectedPath))
                    .ConfigureAwait(true);

                if (!ReferenceEquals(thread, _activeThread) || !ReferenceEquals(project, _activeProject))
                {
                    return;
                }

                if (thread.BaselineSnapshot is null)
                {
                    thread.BaselineSnapshot = checkpointSnapshot;
                    LogAutomationEvent("git", "thread.baseline_captured", "Captured thread baseline from checkpoint flow", new Dictionary<string, string>
                    {
                        ["threadId"] = thread.Id,
                        ["projectId"] = project.Id,
                        ["selectedPath"] = checkpointSnapshot.SelectedPath ?? string.Empty,
                    });
                }

                WorkspaceDiffCheckpoint checkpoint = new(
                    string.IsNullOrWhiteSpace(checkpointName) ? $"Checkpoint {thread.DiffCheckpoints.Count + 1}" : checkpointName.Trim());
                checkpoint.CapturedAt = DateTimeOffset.UtcNow;
                checkpoint.Snapshot = GitStatusService.CloneSnapshot(checkpointSnapshot);
                thread.DiffCheckpoints.Add(checkpoint);

                RefreshDiffReviewSourceControls();
                QueueSessionSave(SessionSaveDetail.Full);
                LogAutomationEvent("git", "checkpoint.captured", $"Captured {checkpoint.Name}", new Dictionary<string, string>
                {
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["checkpointId"] = checkpoint.Id,
                    ["checkpointName"] = checkpoint.Name,
                    ["selectedPath"] = checkpointSnapshot.SelectedPath ?? string.Empty,
                });
            }
            finally
            {
                _capturingDiffCheckpoint = false;
                RefreshDiffReviewSourceControls();
            }
        }

        private void EnsureThreadBaselineCapture(WorkspaceThread thread, WorkspaceProject project, GitThreadSnapshot liveSnapshot)
        {
            if (thread is null || project is null || thread.BaselineSnapshot is not null)
            {
                RefreshDiffReviewSourceControls();
                return;
            }

            if (!_baselineCaptureInFlightThreadIds.Add(thread.Id))
            {
                RefreshDiffReviewSourceControls();
                return;
            }

            string worktreePath = thread.WorktreePath ?? project.RootPath;
            string selectedPath = liveSnapshot?.SelectedPath;
            _ = System.Threading.Tasks.Task.Run(() => GitStatusService.CaptureComplete(worktreePath, selectedPath))
                .ContinueWith(task =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _baselineCaptureInFlightThreadIds.Remove(thread.Id);
                        if (task.IsFaulted || task.IsCanceled)
                        {
                            LogAutomationEvent("git", "thread.baseline_failed", "Failed to capture thread baseline", new Dictionary<string, string>
                            {
                                ["threadId"] = thread.Id,
                                ["projectId"] = project.Id,
                            });
                            RefreshDiffReviewSourceControls();
                            return;
                        }

                        if (thread.BaselineSnapshot is null)
                        {
                            thread.BaselineSnapshot = task.Result;
                            QueueSessionSave();
                            LogAutomationEvent("git", "thread.baseline_captured", "Captured thread baseline", new Dictionary<string, string>
                            {
                                ["threadId"] = thread.Id,
                                ["projectId"] = project.Id,
                                ["selectedPath"] = task.Result?.SelectedPath ?? string.Empty,
                            });
                        }

                        RefreshDiffReviewSourceControls();
                        if (ReferenceEquals(thread, _activeThread) && thread.DiffReviewSource == DiffReviewSourceKind.Baseline)
                        {
                            ApplyGitSnapshotToUi();
                        }
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
            RefreshDiffReviewSourceControls();
        }
    }
}
