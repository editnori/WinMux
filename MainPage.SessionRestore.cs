using Microsoft.UI.Dispatching;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Git;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Persistence;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SelfContainedDeployment
{
    public partial class MainPage
    {
        private void OnSessionSaveTimerTick(DispatcherQueueTimer sender, object args)
        {
            _sessionSaveTimer.Stop();
            if (_sessionSaveInFlight)
            {
                _sessionSavePending = true;
                return;
            }

            PersistSessionState(backgroundWrite: true);
        }

        private void InitializeShellModel()
        {
            if (TryRestoreSession())
            {
                LogAutomationEvent("shell", "workspace.restored", "Restored previous WinMux session", new Dictionary<string, string>
                {
                    ["projectCount"] = _projects.Count.ToString(),
                    ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                });
                return;
            }

            WorkspaceProject project = GetOrCreateProject(ResolveWorkspaceBootstrapPath(), null, SampleConfig.DefaultShellProfileId);
            if (project.Threads.Count == 0)
            {
                CreateThread(project);
            }

            ActivateProject(project);
            LogAutomationEvent("shell", "workspace.initialized", "Initialized workspace model", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["projectPath"] = project.RootPath,
            });
        }

        internal void PersistSessionState()
        {
            PersistSessionState(backgroundWrite: false);
        }

        private void PersistSessionState(bool backgroundWrite)
        {
            if (_restoringSession)
            {
                return;
            }

            if (backgroundWrite)
            {
                if (_sessionSaveInFlight)
                {
                    _sessionSavePending = true;
                    return;
                }

                _sessionSaveInFlight = true;
            }

            NativeAutomationDiagnostics.IncrementCounter("autosave.count");
            SessionSaveDetail saveDetail = backgroundWrite
                ? _pendingSessionSaveDetail
                : SessionSaveDetail.Full;
            _pendingSessionSaveDetail = SessionSaveDetail.Lightweight;
            WorkspaceSessionSnapshot snapshot;
            Dictionary<string, string> logData;
            try
            {
                snapshot = BuildSessionSnapshot(saveDetail);
                logData = new()
                {
                    ["projectCount"] = snapshot.Projects.Count.ToString(),
                    ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                    ["saveDetail"] = saveDetail.ToString().ToLowerInvariant(),
                };
            }
            catch
            {
                if (backgroundWrite)
                {
                    _sessionSaveInFlight = false;
                }

                throw;
            }

            var perfData = new Dictionary<string, string>(logData, StringComparer.Ordinal)
            {
                ["background"] = backgroundWrite.ToString(),
                ["reason"] = backgroundWrite ? "autosave" : "explicit-save",
            };

            if (!backgroundWrite)
            {
                using var perfScope = NativeAutomationDiagnostics.TrackOperation("workspace.save", data: perfData);
                WorkspaceSessionStore.Save(snapshot);
                LogAutomationEvent("shell", "workspace.saved", "Saved WinMux workspace session", logData);
                return;
            }

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var perfScope = NativeAutomationDiagnostics.TrackOperation("workspace.save", background: true, data: perfData);
                    WorkspaceSessionStore.Save(snapshot);
                    LogAutomationEvent("shell", "workspace.saved", "Saved WinMux workspace session", logData);
                }
                catch (Exception ex)
                {
                    LogAutomationEvent("shell", "workspace.save_failed", $"Could not save workspace session: {ex.Message}", new Dictionary<string, string>
                    {
                        ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                    });
                }
                finally
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        _sessionSaveInFlight = false;
                        if (_sessionSavePending)
                        {
                            _sessionSavePending = false;
                            _sessionSaveTimer.Stop();
                            _sessionSaveTimer.Start();
                        }
                    });
                }
            });
        }

        private void QueueSessionSave(SessionSaveDetail detail = SessionSaveDetail.Lightweight)
        {
            if (_restoringSession)
            {
                return;
            }

            if (detail == SessionSaveDetail.Full)
            {
                _pendingSessionSaveDetail = SessionSaveDetail.Full;
            }

            _sessionSaveTimer.Stop();
            _sessionSaveTimer.Start();
        }

        private WorkspaceSessionSnapshot BuildSessionSnapshot(SessionSaveDetail detail)
        {
            bool includeGitSnapshots = detail == SessionSaveDetail.Full;
            return new WorkspaceSessionSnapshot
            {
                SavedAt = DateTimeOffset.UtcNow.ToString("O"),
                Theme = WorkspaceSessionStore.FormatTheme(SampleConfig.CurrentTheme),
                DefaultShellProfileId = SampleConfig.DefaultShellProfileId,
                MaxPaneCountPerThread = SampleConfig.MaxPaneCountPerThread,
                PaneOpen = ShellSplitView.IsPaneOpen,
                InspectorOpen = _inspectorOpen,
                ActiveView = ResolveActiveViewName(),
                ActiveProjectId = _activeProject?.Id,
                ActiveThreadId = _activeThread?.Id,
                ThreadSequence = _threadSequence,
                Projects = _projects
                    .Where(ShouldPersistProject)
                    .Select(project => new ProjectSessionSnapshot
                    {
                        Id = project.Id,
                        Name = project.Name,
                        RootPath = project.RootPath,
                        ShellProfileId = project.ShellProfileId,
                        SelectedThreadId = project.SelectedThreadId,
                        Threads = project.Threads.Select(thread => new ThreadSessionSnapshot
                        {
                            Id = thread.Id,
                            Name = thread.Name,
                            WorktreePath = thread.WorktreePath,
                            BranchName = thread.BranchName,
                            Notes = thread.Notes,
                            SelectedNoteId = thread.SelectedNoteId,
                            NoteEntries = thread.NoteEntries.Select(note => new ThreadNoteSessionSnapshot
                            {
                                Id = note.Id,
                                Title = note.Title,
                                Text = note.Text,
                                PaneId = note.PaneId,
                                CreatedAt = note.CreatedAt.ToString("O"),
                                UpdatedAt = note.UpdatedAt.ToString("O"),
                                ArchivedAt = note.ArchivedAt?.ToString("O"),
                            }).ToList(),
                            SelectedDiffPath = thread.SelectedDiffPath,
                            DiffReviewSource = FormatDiffReviewSource(thread.DiffReviewSource),
                            SelectedCheckpointId = thread.SelectedCheckpointId,
                            BaselineSnapshot = includeGitSnapshots ? CreateGitSnapshotSessionSnapshot(thread.BaselineSnapshot) : null,
                            LiveSnapshot = CreateGitSnapshotSessionSnapshot(thread.LiveSnapshot),
                            LiveSnapshotCapturedAt = thread.LiveSnapshotCapturedAt == default ? null : thread.LiveSnapshotCapturedAt.ToString("O"),
                            SelectedPaneId = thread.SelectedPaneId,
                            Layout = WorkspaceSessionStore.FormatLayout(thread.LayoutPreset),
                            PrimarySplitRatio = thread.PrimarySplitRatio,
                            SecondarySplitRatio = thread.SecondarySplitRatio,
                            AutoFitPaneContentLocked = thread.AutoFitPaneContentLocked,
                            DiffCheckpoints = includeGitSnapshots
                                ? thread.DiffCheckpoints.Select(checkpoint => new GitCheckpointSessionSnapshot
                                {
                                    Id = checkpoint.Id,
                                    Name = checkpoint.Name,
                                    CapturedAt = checkpoint.CapturedAt.ToString("O"),
                                    Snapshot = CreateGitSnapshotSessionSnapshot(checkpoint.Snapshot),
                                }).ToList()
                                : new List<GitCheckpointSessionSnapshot>(),
                            Panes = thread.Panes
                                .Where(ShouldPersistPane)
                                .Select(CreatePaneSessionSnapshot)
                                .Where(paneSnapshot => paneSnapshot is not null)
                                .ToList(),
                        }).ToList(),
                    }).ToList(),
            };
        }

        private static bool ShouldPersistPane(WorkspacePaneRecord pane)
        {
            return pane is not null && (!pane.IsExited || pane.PersistExitedState);
        }

        private static PaneSessionSnapshot CreatePaneSessionSnapshot(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return null;
            }

            if (pane is DeferredPaneRecord deferredPane)
            {
                PaneSessionSnapshot snapshot = deferredPane.Snapshot;
                return new PaneSessionSnapshot
                {
                    Id = pane.Id,
                    Kind = pane.Kind.ToString().ToLowerInvariant(),
                    Title = pane.Title,
                    HasCustomTitle = pane.HasCustomTitle,
                    IsExited = pane.IsExited,
                    ReplayRestoreFailed = pane.ReplayRestoreFailed,
                    BrowserUri = deferredPane.BrowserUri,
                    SelectedBrowserTabId = deferredPane.SelectedBrowserTabId,
                    BrowserTabs = deferredPane.BrowserTabs.Select(tab => new BrowserTabSessionSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    }).ToList(),
                    DiffPath = deferredPane.DiffPath,
                    EditorFilePath = deferredPane.EditorFilePath,
                    ReplayTool = pane.ReplayTool,
                    ReplaySessionId = pane.ReplaySessionId,
                    ReplayCommand = pane.ReplayCommand,
                    ReplayArguments = pane.ReplayArguments,
                };
            }

            return new PaneSessionSnapshot
            {
                Id = pane.Id,
                Kind = pane.Kind.ToString().ToLowerInvariant(),
                Title = pane.Title,
                HasCustomTitle = pane.HasCustomTitle,
                IsExited = pane.IsExited,
                ReplayRestoreFailed = pane.ReplayRestoreFailed,
                BrowserUri = pane is BrowserPaneRecord browserPane ? browserPane.Browser.CurrentUri : null,
                SelectedBrowserTabId = pane is BrowserPaneRecord browserPaneState ? browserPaneState.Browser.SelectedTabId : null,
                BrowserTabs = pane is BrowserPaneRecord browserPaneTabs
                    ? browserPaneTabs.Browser.Tabs.Select(tab => new BrowserTabSessionSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    }).ToList()
                    : new List<BrowserTabSessionSnapshot>(),
                DiffPath = pane is DiffPaneRecord diffPane ? diffPane.DiffPath : null,
                EditorFilePath = pane is EditorPaneRecord editorPane ? editorPane.Editor.SelectedFilePath : null,
                ReplayTool = pane.ReplayTool,
                ReplaySessionId = pane.ReplaySessionId,
                ReplayCommand = pane.ReplayCommand,
                ReplayArguments = pane.ReplayArguments,
            };
        }

        private static string BuildBrowserPersistenceKey(BrowserPaneControl browser)
        {
            if (browser is null)
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            builder.Append(browser.SelectedTabId ?? string.Empty)
                .Append('|')
                .Append(browser.CurrentUri ?? string.Empty)
                .Append('|');

            foreach (BrowserPaneControl.BrowserPaneTabSnapshot tab in browser.Tabs)
            {
                builder.Append(tab.Id ?? string.Empty)
                    .Append(':')
                    .Append(tab.Uri ?? string.Empty)
                    .Append('|');
            }

            return builder.ToString();
        }

        private static GitSnapshotSessionSnapshot CreateGitSnapshotSessionSnapshot(GitThreadSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            bool persistFullDiffText = ShouldPersistFullDiffText(snapshot);

            return new GitSnapshotSessionSnapshot
            {
                BranchName = snapshot.BranchName,
                RepositoryRootPath = snapshot.RepositoryRootPath,
                WorktreePath = snapshot.WorktreePath,
                StatusSummary = snapshot.StatusSummary,
                DiffSummary = snapshot.DiffSummary,
                SelectedPath = snapshot.SelectedPath,
                SelectedDiff = snapshot.SelectedDiff,
                Error = snapshot.Error,
                ChangedFiles = snapshot.ChangedFiles.Select(file => new GitChangedFileSessionSnapshot
                {
                    Status = file.Status,
                    Path = file.Path,
                    OriginalPath = file.OriginalPath,
                    AddedLines = file.AddedLines,
                    RemovedLines = file.RemovedLines,
                    DiffText = ShouldPersistDiffText(snapshot, file, persistFullDiffText) ? file.DiffText : null,
                }).ToList(),
            };
        }

        private static bool ShouldPersistFullDiffText(GitThreadSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return false;
            }

            int diffFileCount = 0;
            int totalDiffChars = 0;
            foreach (GitChangedFile file in snapshot.ChangedFiles)
            {
                if (string.IsNullOrWhiteSpace(file?.DiffText))
                {
                    continue;
                }

                diffFileCount++;
                totalDiffChars += file.DiffText.Length;
                if (diffFileCount > MaxPersistedSnapshotDiffFiles || totalDiffChars > MaxPersistedSnapshotDiffChars)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ShouldPersistDiffText(GitThreadSnapshot snapshot, GitChangedFile file, bool persistFullDiffText)
        {
            if (persistFullDiffText)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(snapshot?.SelectedPath) &&
                string.Equals(file?.Path, snapshot.SelectedPath, StringComparison.Ordinal);
        }

        private static GitThreadSnapshot RestoreGitThreadSnapshot(GitSnapshotSessionSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            GitThreadSnapshot restored = new()
            {
                BranchName = snapshot.BranchName,
                RepositoryRootPath = snapshot.RepositoryRootPath,
                WorktreePath = snapshot.WorktreePath,
                StatusSummary = snapshot.StatusSummary,
                DiffSummary = snapshot.DiffSummary,
                SelectedPath = snapshot.SelectedPath,
                SelectedDiff = snapshot.SelectedDiff,
                Error = snapshot.Error,
                ChangedFiles = (snapshot.ChangedFiles ?? new List<GitChangedFileSessionSnapshot>()).Select(file => new GitChangedFile
                {
                    Status = file.Status,
                    Path = file.Path,
                    OriginalPath = file.OriginalPath,
                    AddedLines = file.AddedLines,
                    RemovedLines = file.RemovedLines,
                    DiffText = file.DiffText,
                }).ToList(),
            };
            GitStatusService.SelectDiffPath(restored, restored.SelectedPath);
            return restored;
        }

        private string ResolveActiveViewName()
        {
            if (_showingSettings)
            {
                return "settings";
            }

            return "terminal";
        }

        private static string FormatInspectorSection(InspectorSection section)
        {
            return section switch
            {
                InspectorSection.Files => "files",
                InspectorSection.Notes => "notes",
                _ => "review",
            };
        }

        private bool TryRestoreSession()
        {
            WorkspaceSessionSnapshot snapshot = WorkspaceSessionStore.Load(out string loadError);
            var perfData = new Dictionary<string, string>
            {
                ["savedProjectCount"] = (snapshot?.Projects?.Count ?? 0).ToString(),
                ["loadError"] = loadError ?? string.Empty,
            };
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("workspace.restore", data: perfData);
            if (snapshot?.Projects is null || snapshot.Projects.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(loadError))
                {
                    LogAutomationEvent("shell", "workspace.restore_failed", $"Could not load saved workspace session: {loadError}", new Dictionary<string, string>
                    {
                        ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                    });
                }

                return false;
            }

            _restoringSession = true;
            try
            {
                DisposeAllWorkspacePanes();
                _projects.Clear();
                _tabItemsById.Clear();
                _paneContainersById.Clear();

                SampleConfig.CurrentTheme = WorkspaceSessionStore.ParseTheme(snapshot.Theme);
                SampleConfig.DefaultShellProfileId = ShellProfiles.Resolve(snapshot.DefaultShellProfileId).Id;
                SampleConfig.MaxPaneCountPerThread = Math.Clamp(snapshot.MaxPaneCountPerThread, 2, 4);
                _threadSequence = Math.Max(1, snapshot.ThreadSequence);
                int skippedMissingProjectCount = 0;

                foreach (ProjectSessionSnapshot projectSnapshot in snapshot.Projects)
                {
                    if (!ShouldPersistProjectPath(projectSnapshot.RootPath))
                    {
                        continue;
                    }

                    if (!TryResolveRestorableProjectPath(projectSnapshot.RootPath, out string restorableProjectPath, out string unavailableProjectPath))
                    {
                        skippedMissingProjectCount++;
                        LogAutomationEvent("shell", "workspace.restore_project_skipped", "Skipped restoring a project because its root path is unavailable on this machine.", new Dictionary<string, string>
                        {
                            ["projectId"] = projectSnapshot.Id ?? string.Empty,
                            ["projectName"] = projectSnapshot.Name ?? string.Empty,
                            ["projectPath"] = projectSnapshot.RootPath ?? string.Empty,
                            ["unavailablePath"] = unavailableProjectPath ?? string.Empty,
                        });
                        continue;
                    }

                    WorkspaceProject project = new(restorableProjectPath, projectSnapshot.ShellProfileId, projectSnapshot.Name, projectSnapshot.Id);
                    _projects.Add(project);

                    foreach (ThreadSessionSnapshot threadSnapshot in projectSnapshot.Threads ?? new List<ThreadSessionSnapshot>())
                    {
                        WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadSnapshot.Name) ? $"Thread {_threadSequence++}" : threadSnapshot.Name, threadSnapshot.Id)
                        {
                            WorktreePath = ResolveRequestedPath(string.IsNullOrWhiteSpace(threadSnapshot.WorktreePath) ? project.RootPath : threadSnapshot.WorktreePath),
                            BranchName = threadSnapshot.BranchName,
                            ChangedFileCount = 0,
                            SelectedNoteId = threadSnapshot.SelectedNoteId,
                            SelectedDiffPath = threadSnapshot.SelectedDiffPath,
                            DiffReviewSource = ParseDiffReviewSource(threadSnapshot.DiffReviewSource),
                            SelectedCheckpointId = threadSnapshot.SelectedCheckpointId,
                            BaselineSnapshot = RestoreGitThreadSnapshot(threadSnapshot.BaselineSnapshot),
                            LiveSnapshot = RestoreGitThreadSnapshot(threadSnapshot.LiveSnapshot),
                            LiveSnapshotCapturedAt = DateTimeOffset.TryParse(threadSnapshot.LiveSnapshotCapturedAt, out DateTimeOffset liveSnapshotCapturedAt)
                                ? liveSnapshotCapturedAt
                                : default,
                            LayoutPreset = WorkspaceSessionStore.ParseLayout(threadSnapshot.Layout),
                            PrimarySplitRatio = ClampPaneSplitRatio(threadSnapshot.PrimarySplitRatio <= 0 ? 0.58 : threadSnapshot.PrimarySplitRatio),
                            SecondarySplitRatio = ClampPaneSplitRatio(threadSnapshot.SecondarySplitRatio <= 0 ? 0.5 : threadSnapshot.SecondarySplitRatio),
                            AutoFitPaneContentLocked = threadSnapshot.AutoFitPaneContentLocked,
                        };
                        if (thread.LiveSnapshot is not null)
                        {
                            thread.BranchName = string.IsNullOrWhiteSpace(thread.LiveSnapshot.BranchName)
                                ? thread.BranchName
                                : thread.LiveSnapshot.BranchName;
                            thread.ChangedFileCount = thread.LiveSnapshot.ChangedFiles.Count;
                            thread.SelectedDiffPath = string.IsNullOrWhiteSpace(thread.LiveSnapshot.SelectedPath)
                                ? thread.SelectedDiffPath
                                : thread.LiveSnapshot.SelectedPath;
                        }

                        foreach (ThreadNoteSessionSnapshot noteSnapshot in threadSnapshot.NoteEntries ?? new List<ThreadNoteSessionSnapshot>())
                        {
                            WorkspaceThreadNote note = new(noteSnapshot.Title, noteSnapshot.Text, noteSnapshot.Id)
                            {
                                PaneId = noteSnapshot.PaneId,
                                CreatedAt = DateTimeOffset.TryParse(noteSnapshot.CreatedAt, out DateTimeOffset createdAt)
                                    ? createdAt
                                    : DateTimeOffset.UtcNow,
                                UpdatedAt = DateTimeOffset.TryParse(noteSnapshot.UpdatedAt, out DateTimeOffset updatedAt)
                                    ? updatedAt
                                    : DateTimeOffset.UtcNow,
                                ArchivedAt = DateTimeOffset.TryParse(noteSnapshot.ArchivedAt, out DateTimeOffset archivedAt)
                                    ? archivedAt
                                    : null,
                            };
                            thread.NoteEntries.Add(note);
                        }

                        if (thread.NoteEntries.Count == 0 && !string.IsNullOrWhiteSpace(threadSnapshot.Notes))
                        {
                            thread.Notes = threadSnapshot.Notes;
                        }

                        if (thread.NoteEntries.Count > 0 &&
                            !thread.NoteEntries.Any(candidate => string.Equals(candidate.Id, thread.SelectedNoteId, StringComparison.Ordinal)))
                        {
                            thread.SelectedNoteId = ResolvePreferredThreadNote(thread)?.Id;
                        }

                        foreach (GitCheckpointSessionSnapshot checkpointSnapshot in threadSnapshot.DiffCheckpoints ?? new List<GitCheckpointSessionSnapshot>())
                        {
                            thread.DiffCheckpoints.Add(new WorkspaceDiffCheckpoint(checkpointSnapshot.Name, checkpointSnapshot.Id)
                            {
                                CapturedAt = DateTimeOffset.TryParse(checkpointSnapshot.CapturedAt, out DateTimeOffset capturedAt)
                                    ? capturedAt
                                    : DateTimeOffset.UtcNow,
                                Snapshot = RestoreGitThreadSnapshot(checkpointSnapshot.Snapshot),
                            });
                        }

                        NormalizeDiffReviewSource(thread);
                        project.Threads.Add(thread);

                        foreach (PaneSessionSnapshot paneSnapshot in threadSnapshot.Panes ?? new List<PaneSessionSnapshot>())
                        {
                            try
                            {
                                WorkspacePaneRecord pane = RestorePaneFromSnapshot(project, thread, paneSnapshot, materialize: false);
                                if (pane is not null)
                                {
                                    thread.Panes.Add(pane);
                                }
                            }
                            catch (Exception ex)
                            {
                                LogAutomationEvent("shell", "workspace.restore_pane_failed", $"Could not restore pane {paneSnapshot.Id}: {ex.Message}", new Dictionary<string, string>
                                {
                                    ["projectId"] = project.Id,
                                    ["threadId"] = thread.Id,
                                    ["paneId"] = paneSnapshot.Id ?? string.Empty,
                                    ["paneKind"] = paneSnapshot.Kind ?? string.Empty,
                                });
                            }
                        }

                        if (thread.Panes.Count == 0)
                        {
                            EnsureThreadHasTab(project, thread);
                        }

                        thread.SelectedPaneId = thread.Panes.Any(candidate => string.Equals(candidate.Id, threadSnapshot.SelectedPaneId, StringComparison.Ordinal))
                            ? threadSnapshot.SelectedPaneId
                            : thread.Panes.FirstOrDefault()?.Id;
                    }

                    if (project.Threads.Count == 0)
                    {
                        CreateThread(project);
                    }

                    project.SelectedThreadId = project.Threads.Any(candidate => string.Equals(candidate.Id, projectSnapshot.SelectedThreadId, StringComparison.Ordinal))
                        ? projectSnapshot.SelectedThreadId
                        : project.Threads.FirstOrDefault()?.Id;
                }

                if (_projects.Count == 0)
                {
                    if (skippedMissingProjectCount > 0)
                    {
                        LogAutomationEvent("shell", "workspace.restore_empty", "Skipped all saved projects because their roots were unavailable on this machine.", new Dictionary<string, string>
                        {
                            ["skippedProjectCount"] = skippedMissingProjectCount.ToString(),
                            ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                        });
                    }

                    return false;
                }

                _activeProject = _projects.FirstOrDefault(project => string.Equals(project.Id, snapshot.ActiveProjectId, StringComparison.Ordinal))
                    ?? _projects.FirstOrDefault();
                _activeThread = _projects
                    .SelectMany(project => project.Threads)
                    .FirstOrDefault(thread => string.Equals(thread.Id, snapshot.ActiveThreadId, StringComparison.Ordinal))
                    ?? _activeProject?.Threads.FirstOrDefault(thread => string.Equals(thread.Id, _activeProject.SelectedThreadId, StringComparison.Ordinal))
                    ?? _activeProject?.Threads.FirstOrDefault();

                if (_activeProject is not null && _activeThread is not null)
                {
                    EnsureThreadPanesMaterialized(_activeProject, _activeThread);
                    _activeProject.SelectedThreadId = _activeThread.Id;
                    _activeGitSnapshot = _activeThread.LiveSnapshot;
                }

                perfData["restoredProjectCount"] = _projects.Count.ToString();
                perfData["activeProjectId"] = _activeProject?.Id ?? string.Empty;
                perfData["activeThreadId"] = _activeThread?.Id ?? string.Empty;

                ShellSplitView.IsPaneOpen = snapshot.PaneOpen;
                _inspectorOpen = snapshot.InspectorOpen;
                _showingSettings = string.Equals(snapshot.ActiveView, "settings", StringComparison.OrdinalIgnoreCase);
                RefreshProjectTree();
                RefreshTabView();
                UpdateInspectorVisibility();
                return true;
            }
            catch (Exception ex)
            {
                LogAutomationEvent("shell", "workspace.restore_failed", $"Could not restore saved workspace session: {ex.Message}", new Dictionary<string, string>
                {
                    ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                });
                DisposeAllWorkspacePanes();
                _projects.Clear();
                _tabItemsById.Clear();
                _paneContainersById.Clear();
                _activeProject = null;
                _activeThread = null;
                return false;
            }
            finally
            {
                _restoringSession = false;
            }
        }

        private void EnsureThreadPanesMaterialized(WorkspaceProject project, WorkspaceThread thread)
        {
            if (project is null || thread is null || thread.Panes.Count == 0 || thread.Panes.All(pane => !pane.IsDeferred))
            {
                return;
            }

            string selectedPaneId = thread.SelectedPaneId;
            if (string.IsNullOrWhiteSpace(selectedPaneId))
            {
                selectedPaneId = thread.Panes.FirstOrDefault()?.Id;
            }

            HashSet<string> paneIdsToMaterialize = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(selectedPaneId))
            {
                paneIdsToMaterialize.Add(selectedPaneId);
            }

            if (!string.IsNullOrWhiteSpace(thread.ZoomedPaneId))
            {
                paneIdsToMaterialize.Add(thread.ZoomedPaneId);
            }

            for (int index = 0; index < thread.Panes.Count; index++)
            {
                if (thread.Panes[index] is not DeferredPaneRecord deferredPane ||
                    !paneIdsToMaterialize.Contains(deferredPane.Id))
                {
                    continue;
                }

                WorkspacePaneRecord materializedPane = RestorePaneFromSnapshot(project, thread, deferredPane.Snapshot, materialize: true);
                if (materializedPane is null)
                {
                    continue;
                }

                thread.Panes[index] = materializedPane;
            }

            if (thread.Panes.Count == 0)
            {
                EnsureThreadHasTab(project, thread);
            }

            if (thread.Panes.Any(candidate => string.Equals(candidate.Id, selectedPaneId, StringComparison.Ordinal)))
            {
                thread.SelectedPaneId = selectedPaneId;
            }
            else
            {
                thread.SelectedPaneId = thread.Panes.FirstOrDefault()?.Id;
            }
        }

        private void QueueVisibleDeferredPaneMaterialization(WorkspaceProject project, WorkspaceThread thread)
        {
            if (!EnableVisibleDeferredPaneMaterialization)
            {
                return;
            }

            int requestId = ++_visibleDeferredPaneMaterializationRequestId;
            if (project is null ||
                thread is null ||
                _showingSettings ||
                !ReferenceEquals(project, _activeProject) ||
                !ReferenceEquals(thread, _activeThread))
            {
                return;
            }

            List<string> deferredPaneIds = GetVisiblePanes(thread)
                .Where(candidate => candidate.IsDeferred &&
                    !string.Equals(candidate.Id, thread.SelectedPaneId, StringComparison.Ordinal))
                .Select(candidate => candidate.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            if (deferredPaneIds.Count == 0)
            {
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(60).ConfigureAwait(false);
                    foreach (string paneId in deferredPaneIds)
                    {
                        await EnqueueOnUiThreadAsync(() =>
                        {
                            if (requestId != _visibleDeferredPaneMaterializationRequestId ||
                                _showingSettings ||
                                !ReferenceEquals(project, _activeProject) ||
                                !ReferenceEquals(thread, _activeThread))
                            {
                                return;
                            }

                            _ = MaterializeDeferredPane(project, thread, paneId);
                        }).ConfigureAwait(false);

                        if (requestId != _visibleDeferredPaneMaterializationRequestId)
                        {
                            return;
                        }

                        await System.Threading.Tasks.Task.Delay(35).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            });
        }

        private bool MaterializeDeferredPane(WorkspaceProject project, WorkspaceThread thread, string paneId)
        {
            if (project is null || thread is null || string.IsNullOrWhiteSpace(paneId))
            {
                return false;
            }

            int paneIndex = thread.Panes.FindIndex(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal));
            if (paneIndex < 0 || thread.Panes[paneIndex] is not DeferredPaneRecord deferredPane)
            {
                return false;
            }

            var perfData = new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["threadId"] = thread.Id,
                ["paneId"] = paneId,
                ["paneKind"] = deferredPane.Kind.ToString().ToLowerInvariant(),
                ["reason"] = "deferred-materialize",
            };
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("pane.materialize", data: perfData);

            WorkspacePaneRecord materializedPane = RestorePaneFromSnapshot(project, thread, deferredPane.Snapshot, materialize: true);
            if (materializedPane is null)
            {
                return false;
            }

            thread.Panes[paneIndex] = materializedPane;
            UpdateTabViewItem(materializedPane);
            if (!ReferenceEquals(thread, _activeThread))
            {
                return true;
            }

            if (materializedPane.Kind == WorkspacePaneKind.Diff)
            {
                ApplyGitSnapshotToUi();
                QueueVisibleDiffHydrationIfNeeded(thread, project, _activeGitSnapshot ?? thread.LiveSnapshot);
            }

            _lastPaneWorkspaceRenderKey = null;
            RenderPaneWorkspace();
            RequestLayoutForVisiblePanes();
            return true;
        }

        private void EnsureThreadAllPanesMaterialized(WorkspaceProject project, WorkspaceThread thread)
        {
            if (project is null || thread is null || thread.Panes.Count == 0 || thread.Panes.All(pane => !pane.IsDeferred))
            {
                return;
            }

            string selectedPaneId = thread.SelectedPaneId;
            for (int index = 0; index < thread.Panes.Count; index++)
            {
                if (thread.Panes[index] is not DeferredPaneRecord deferredPane)
                {
                    continue;
                }

                WorkspacePaneRecord materializedPane = RestorePaneFromSnapshot(project, thread, deferredPane.Snapshot, materialize: true);
                if (materializedPane is null)
                {
                    continue;
                }

                thread.Panes[index] = materializedPane;
            }

            if (thread.Panes.Count == 0)
            {
                EnsureThreadHasTab(project, thread);
            }

            if (thread.Panes.Any(candidate => string.Equals(candidate.Id, selectedPaneId, StringComparison.Ordinal)))
            {
                thread.SelectedPaneId = selectedPaneId;
            }
            else
            {
                thread.SelectedPaneId = thread.Panes.FirstOrDefault()?.Id;
            }
        }

        private WorkspacePaneRecord RestorePaneFromSnapshot(WorkspaceProject project, WorkspaceThread thread, PaneSessionSnapshot snapshot, bool materialize = true)
        {
            if (snapshot is null)
            {
                return null;
            }

            if (!materialize)
            {
                return new DeferredPaneRecord(snapshot);
            }

            bool hasReplayMetadata = HasReplayRestoreMetadata(snapshot);
            bool replayRestoreRejected = false;
            string restoreReplayCommand = null;
            if (!snapshot.IsExited && hasReplayMetadata)
            {
                replayRestoreRejected = !TryResolveRestoreReplayCommand(snapshot, out restoreReplayCommand);
            }

            bool autoStartSession = !snapshot.IsExited && !replayRestoreRejected;
            string suspendedStatusText = snapshot.IsExited && snapshot.ReplayRestoreFailed
                ? "Replay restore failed last time. Close the tab or reopen the saved session manually."
                : replayRestoreRejected
                    ? "Saved replay metadata could not be restored automatically. Resume the saved session manually."
                    : null;

            WorkspacePaneRecord pane = snapshot.Kind?.Trim().ToLowerInvariant() switch
            {
                "browser" => CreateBrowserPane(project, thread, snapshot.BrowserUri, string.IsNullOrWhiteSpace(snapshot.Title) ? "Preview" : snapshot.Title, snapshot.Id),
                "diff" => CreateDiffPane(
                    project,
                    thread,
                    snapshot.DiffPath,
                    diffText: null,
                    string.IsNullOrWhiteSpace(snapshot.Title) ? BuildDiffPaneTitle(snapshot.DiffPath) : snapshot.Title,
                    paneId: snapshot.Id),
                "editor" => CreateEditorPane(project, thread, snapshot.EditorFilePath ?? thread.SelectedDiffPath, string.IsNullOrWhiteSpace(snapshot.Title) ? "Editor" : snapshot.Title, snapshot.Id),
                _ => CreateTerminalPane(
                    project,
                    thread,
                    WorkspacePaneKind.Terminal,
                    startupInput: null,
                    initialTitle: string.IsNullOrWhiteSpace(snapshot.Title) ? "Terminal" : snapshot.Title,
                    paneId: snapshot.Id,
                    restoreReplayCommand: restoreReplayCommand,
                    autoStartSession: autoStartSession,
                    suspendedStatusText: suspendedStatusText),
            };

            pane.HasCustomTitle = snapshot.HasCustomTitle;
            if (snapshot.HasCustomTitle && !string.IsNullOrWhiteSpace(snapshot.Title))
            {
                pane.Title = snapshot.Title;
            }

            pane.ReplayTool = snapshot.ReplayTool;
            pane.ReplaySessionId = snapshot.ReplaySessionId;
            pane.ReplayCommand = restoreReplayCommand;
            pane.ReplayArguments = snapshot.ReplayArguments;
            pane.RestoredFromSession = true;
            pane.ReplayRestoreFailed = snapshot.ReplayRestoreFailed || replayRestoreRejected;
            pane.PersistExitedState = snapshot.IsExited && snapshot.ReplayRestoreFailed || replayRestoreRejected;
            if (snapshot.IsExited && pane is TerminalPaneRecord exitedPane)
            {
                exitedPane.MarkExited();
            }
            else if (!string.IsNullOrWhiteSpace(restoreReplayCommand) && pane is TerminalPaneRecord replayPane)
            {
                replayPane.MarkReplayRestorePending();
            }
            else if (replayRestoreRejected && pane is TerminalPaneRecord rejectedReplayPane)
            {
                rejectedReplayPane.MarkExited();
                rejectedReplayPane.MarkReplayRestoreFailed();
            }

            if (pane is BrowserPaneRecord browserPane && snapshot.BrowserTabs?.Count > 0)
            {
                browserPane.Browser.RestoreTabSession(
                    snapshot.BrowserTabs.Select(tab => new BrowserPaneControl.BrowserPaneTabSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    }).ToList(),
                    snapshot.SelectedBrowserTabId);
            }

            return pane;
        }

        private static bool HasReplayRestoreMetadata(PaneSessionSnapshot snapshot)
        {
            return !string.IsNullOrWhiteSpace(snapshot?.ReplayTool) ||
                !string.IsNullOrWhiteSpace(snapshot?.ReplaySessionId) ||
                !string.IsNullOrWhiteSpace(snapshot?.ReplayCommand);
        }

        private static bool TryResolveRestoreReplayCommand(PaneSessionSnapshot snapshot, out string restoreReplayCommand)
        {
            restoreReplayCommand = null;
            if (snapshot is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ReplayArguments) &&
                TerminalControl.TryBuildReplayRestoreCommand(snapshot.ReplayTool, snapshot.ReplaySessionId, snapshot.ReplayArguments, out restoreReplayCommand))
            {
                return true;
            }

            if (!TerminalControl.TryExtractReplayCommandMetadata(snapshot.ReplayCommand, out string replayTool, out string replaySessionId, out string replayArguments))
            {
                return TerminalControl.TryBuildReplayRestoreCommand(snapshot.ReplayTool, snapshot.ReplaySessionId, snapshot.ReplayArguments, out restoreReplayCommand);
            }

            snapshot.ReplayTool = replayTool;
            snapshot.ReplaySessionId = replaySessionId;
            snapshot.ReplayArguments = replayArguments;
            return TerminalControl.TryBuildReplayRestoreCommand(replayTool, replaySessionId, replayArguments, out restoreReplayCommand);
        }

        private static string ResolveRequestedPath(string rootPath)
        {
            return ShellProfiles.NormalizeProjectPath(rootPath);
        }

        private static bool TryResolveRestorableProjectPath(string rootPath, out string normalizedPath, out string unavailablePath)
        {
            normalizedPath = ResolveRequestedPath(rootPath);
            unavailablePath = null;
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                unavailablePath = rootPath;
                return false;
            }

            string pathToCheck = normalizedPath;
            if (ShellProfiles.TryResolveLocalStoragePath(normalizedPath, out string localStoragePath))
            {
                pathToCheck = localStoragePath;
            }
            else if (normalizedPath.StartsWith("/", StringComparison.Ordinal))
            {
                return true;
            }

            if (Directory.Exists(pathToCheck))
            {
                return true;
            }

            unavailablePath = pathToCheck;
            return false;
        }

        private static string ResolveWorkspaceBootstrapPath()
        {
            string currentDirectory = ResolveRequestedPath(Environment.CurrentDirectory);
            if (!LooksLikeInstalledAppDirectory(currentDirectory))
            {
                return currentDirectory;
            }

            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsDirectory) && Directory.Exists(documentsDirectory))
            {
                return ResolveRequestedPath(documentsDirectory);
            }

            string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(homeDirectory) && Directory.Exists(homeDirectory))
            {
                return ResolveRequestedPath(homeDirectory);
            }

            return currentDirectory;
        }

        private static bool LooksLikeInstalledAppDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalizedPath = ResolveRequestedPath(path);
            string baseDirectory = ResolveRequestedPath(AppContext.BaseDirectory);
            if (string.Equals(normalizedPath, baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return File.Exists(Path.Combine(normalizedPath, "WinMux.exe")) ||
                File.Exists(Path.Combine(normalizedPath, "SelfContainedDeployment.exe"));
        }

        private static bool ShouldPersistProject(WorkspaceProject project)
        {
            return project is not null && ShouldPersistProjectPath(project.RootPath);
        }

        private static bool ShouldPersistProjectPath(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return false;
            }

            string normalizedRoot = ShellProfiles.NormalizeProjectPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leafName = Path.GetFileName(normalizedRoot);
            return !leafName.StartsWith("winmux-smoke-", StringComparison.OrdinalIgnoreCase);
        }
    }
}
