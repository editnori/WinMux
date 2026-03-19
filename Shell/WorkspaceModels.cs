using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Git;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Persistence;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SelfContainedDeployment.Shell
{
    public static class ShellProfileIds
    {
        public const string CommandPrompt = "cmd";
        public const string PowerShell = "powershell";
        public const string Wsl = "wsl";
    }

    public sealed class ShellProfileDefinition
    {
        public ShellProfileDefinition(string id, string name, string description, bool usesTranslatedWslPath)
        {
            Id = id;
            Name = name;
            Description = description;
            UsesTranslatedWslPath = usesTranslatedWslPath;
        }

        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public bool UsesTranslatedWslPath { get; }
    }

    public static class ShellProfiles
    {
        private const string WslLocalhostPrefix = @"\\wsl.localhost\";
        private const string WslDollarPrefix = @"\\wsl$\";

        private static readonly IReadOnlyDictionary<string, ShellProfileDefinition> Definitions =
            new Dictionary<string, ShellProfileDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [ShellProfileIds.CommandPrompt] = new ShellProfileDefinition(
                    ShellProfileIds.CommandPrompt,
                    "Command Prompt",
                    "Classic cmd.exe session rooted in the project directory.",
                    usesTranslatedWslPath: false),
                [ShellProfileIds.PowerShell] = new ShellProfileDefinition(
                    ShellProfileIds.PowerShell,
                    "PowerShell",
                    "Windows PowerShell session rooted in the project directory.",
                    usesTranslatedWslPath: false),
                [ShellProfileIds.Wsl] = new ShellProfileDefinition(
                    ShellProfileIds.Wsl,
                    "WSL",
                    "Launch the default WSL distro and start in the translated Linux path.",
                    usesTranslatedWslPath: true),
            };

        public static IReadOnlyList<ShellProfileDefinition> All => Definitions.Values.ToList();

        public static ShellProfileDefinition Resolve(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Definitions[SelfContainedDeployment.SampleConfig.DefaultShellProfileId];
            }

            return Definitions.TryGetValue(id, out ShellProfileDefinition profile)
                ? profile
                : Definitions[SelfContainedDeployment.SampleConfig.DefaultShellProfileId];
        }

        public static string BuildLaunchCommand(string shellProfileId, string projectPath)
        {
            return Resolve(shellProfileId).Id switch
            {
                ShellProfileIds.PowerShell => "powershell.exe -NoLogo",
                ShellProfileIds.Wsl => BuildWslLaunchCommand(projectPath),
                _ => Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            };
        }

        public static string ResolveDisplayPath(string projectPath, string shellProfileId)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (Resolve(shellProfileId).UsesTranslatedWslPath)
            {
                if (TryParseWslSharePath(normalizedPath, out WslSharePath wslSharePath))
                {
                    return wslSharePath.LinuxPath;
                }

                return ToWslPath(normalizedPath);
            }

            return normalizedPath;
        }

        public static string ResolveProcessWorkingDirectory(string projectPath)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);

            if (TryResolveLocalStoragePath(normalizedPath, out string localPath) &&
                Directory.Exists(localPath))
            {
                return localPath;
            }

            return Environment.CurrentDirectory;
        }

        public static string NormalizeProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return Environment.CurrentDirectory;
            }

            string trimmed = projectPath.Trim();
            if (TryParseWslSharePath(trimmed, out WslSharePath wslSharePath))
            {
                return wslSharePath.CanonicalUncPath;
            }

            if (TryConvertToWindowsPath(trimmed, out string windowsPath))
            {
                return Path.GetFullPath(windowsPath);
            }

            if (IsWslPath(trimmed))
            {
                return trimmed.Replace('\\', '/');
            }

            return Path.GetFullPath(trimmed);
        }

        public static bool TryResolveLocalStoragePath(string projectPath, out string localPath)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (!string.IsNullOrWhiteSpace(normalizedPath) &&
                !IsWslPath(normalizedPath) &&
                !TryParseWslSharePath(normalizedPath, out _))
            {
                localPath = normalizedPath;
                return true;
            }

            localPath = null;
            return false;
        }

        public static string ResolveHostReadablePath(string projectPath)
        {
            return TryResolveHostReadablePath(projectPath, out string hostReadablePath)
                ? hostReadablePath
                : null;
        }

        public static bool TryResolveHostReadablePath(string projectPath, out string hostReadablePath)
        {
            hostReadablePath = null;
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return false;
            }

            string normalizedPath = NormalizeProjectPath(projectPath);
            if (TryParseWslSharePath(normalizedPath, out WslSharePath wslSharePath))
            {
                hostReadablePath = Path.GetFullPath(wslSharePath.CanonicalUncPath);
                return true;
            }

            if (TryConvertToWindowsPath(normalizedPath, out string windowsPath))
            {
                hostReadablePath = Path.GetFullPath(windowsPath);
                return true;
            }

            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal) && Path.IsPathRooted(normalizedPath))
            {
                hostReadablePath = Path.GetFullPath(normalizedPath);
                return true;
            }

            return false;
        }

        public static bool EnsureProjectDirectory(string projectPath, out string localPath)
        {
            if (!TryResolveLocalStoragePath(projectPath, out localPath))
            {
                return false;
            }

            Directory.CreateDirectory(localPath);
            return true;
        }

        public static string DeriveName(string path)
        {
            string trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return path;
            }

            int slashIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            if (slashIndex >= 0 && slashIndex < trimmed.Length - 1)
            {
                return trimmed[(slashIndex + 1)..];
            }

            return trimmed;
        }

        private static string ToWslPath(string path)
        {
            if (TryParseWslSharePath(path, out WslSharePath wslSharePath))
            {
                return wslSharePath.LinuxPath;
            }

            if (IsWslPath(path))
            {
                return path.Replace('\\', '/');
            }

            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            {
                return fullPath.Replace('\\', '/');
            }

            char drive = char.ToLowerInvariant(root[0]);
            string suffix = fullPath[root.Length..].Replace('\\', '/').TrimStart('/');
            return string.IsNullOrWhiteSpace(suffix)
                ? $"/mnt/{drive}"
                : $"/mnt/{drive}/{suffix}";
        }

        private static bool IsWslPath(string path)
        {
            return path.StartsWith("/", StringComparison.Ordinal);
        }

        private static bool TryConvertToWindowsPath(string path, out string windowsPath)
        {
            windowsPath = null;
            if (!IsWslPath(path))
            {
                return false;
            }

            string normalized = path.Replace('\\', '/').Trim();
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !string.Equals(segments[0], "mnt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string driveSegment = segments[1];
            if (driveSegment.Length != 1 || !char.IsLetter(driveSegment[0]))
            {
                return false;
            }

            string suffix = string.Join(Path.DirectorySeparatorChar, segments.Skip(2));
            string root = $"{char.ToUpperInvariant(driveSegment[0])}:{Path.DirectorySeparatorChar}";
            windowsPath = string.IsNullOrWhiteSpace(suffix)
                ? root
                : Path.Combine(root, suffix);
            return true;
        }

        private static string BuildWslLaunchCommand(string projectPath)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (TryParseWslSharePath(normalizedPath, out WslSharePath wslSharePath))
            {
                return $"wsl.exe --distribution {wslSharePath.DistroName} --cd \"{wslSharePath.LinuxPath}\"";
            }

            return $"wsl.exe --cd \"{ResolveDisplayPath(normalizedPath, ShellProfileIds.Wsl)}\"";
        }

        private static bool TryParseWslSharePath(string path, out WslSharePath wslSharePath)
        {
            wslSharePath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string trimmed = path.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                trimmed = @"\\" + trimmed[2..].Replace('/', '\\');
            }
            else
            {
                trimmed = trimmed.Replace('/', '\\');
            }

            string prefix = trimmed.StartsWith(WslLocalhostPrefix, StringComparison.OrdinalIgnoreCase)
                ? WslLocalhostPrefix
                : trimmed.StartsWith(WslDollarPrefix, StringComparison.OrdinalIgnoreCase)
                    ? WslDollarPrefix
                    : null;

            if (prefix is null)
            {
                return false;
            }

            string remainder = trimmed[prefix.Length..];
            string[] segments = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            string distroName = segments[0];
            string linuxSuffix = string.Join('/', segments.Skip(1));
            string linuxPath = string.IsNullOrWhiteSpace(linuxSuffix) ? "/" : "/" + linuxSuffix;
            string canonicalUncPath = WslLocalhostPrefix + distroName;
            if (segments.Length > 1)
            {
                canonicalUncPath += @"\" + string.Join('\\', segments.Skip(1));
            }

            wslSharePath = new WslSharePath(distroName, linuxPath, canonicalUncPath);
            return true;
        }

        private sealed record WslSharePath(string DistroName, string LinuxPath, string CanonicalUncPath);
    }

    public sealed class WorkspaceProject
    {
        public WorkspaceProject(string rootPath, string shellProfileId = null, string name = null, string id = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            RootPath = ShellProfiles.NormalizeProjectPath(rootPath);
            ShellProfileId = ShellProfiles.Resolve(shellProfileId).Id;
            Name = string.IsNullOrWhiteSpace(name) ? ShellProfiles.DeriveName(RootPath) : name.Trim();
        }

        public string Id { get; }

        public string Name { get; }

        public string RootPath { get; }

        public string ShellProfileId { get; set; }

        public string DisplayPath => ShellProfiles.ResolveDisplayPath(RootPath, ShellProfileId);

        public string SelectedThreadId { get; set; }

        public List<WorkspaceThread> Threads { get; } = new();
    }

    public enum WorkspacePaneKind
    {
        Terminal,
        Browser,
        Editor,
        Diff,
    }

    public enum WorkspaceLayoutPreset
    {
        Solo = 1,
        Dual = 2,
        Triple = 3,
        Quad = 4,
    }

    public enum DiffReviewSourceKind
    {
        Live = 1,
        Baseline = 2,
        Checkpoint = 3,
    }

    internal sealed class WorkspaceDiffCheckpoint
    {
        public WorkspaceDiffCheckpoint(string name, string id = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            Name = string.IsNullOrWhiteSpace(name) ? "Checkpoint" : name.Trim();
            CapturedAt = DateTimeOffset.UtcNow;
        }

        public string Id { get; }

        public string Name { get; set; }

        public DateTimeOffset CapturedAt { get; set; }

        public GitThreadSnapshot Snapshot { get; set; }
    }

    public sealed class WorkspaceThreadNote
    {
        public WorkspaceThreadNote(string title = null, string text = null, string id = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            Title = string.IsNullOrWhiteSpace(title) ? "Note" : title.Trim();
            Text = text;
            CreatedAt = DateTimeOffset.UtcNow;
            UpdatedAt = CreatedAt;
        }

        public string Id { get; }

        public string Title { get; set; }

        public string Text { get; set; }

        public string PaneId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? ArchivedAt { get; set; }

        public bool IsArchived => ArchivedAt.HasValue;
    }

    public sealed class WorkspaceThread
    {
        private string _worktreePath;

        public WorkspaceThread(WorkspaceProject project, string name, string id = null)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            Name = name;
            WorktreePath = project.RootPath;
            LayoutPreset = WorkspaceLayoutPreset.Dual;
        }

        public string Id { get; }

        public string Name { get; set; }

        public WorkspaceProject Project { get; }

        public string WorktreePath
        {
            get => _worktreePath;
            set
            {
                string normalizedPath = string.IsNullOrWhiteSpace(value)
                    ? null
                    : ShellProfiles.NormalizeProjectPath(value);
                if (string.Equals(_worktreePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _worktreePath = normalizedPath;
                NotifyPaneRoots();
            }
        }

        public string BranchName { get; set; }

        public string SelectedNoteId { get; set; }

        public List<WorkspaceThreadNote> NoteEntries { get; } = new();

        public string Notes
        {
            get => ResolveSelectedNote()?.Text;
            set
            {
                NoteEntries.Clear();
                SelectedNoteId = null;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                WorkspaceThreadNote note = new("Note", value);
                NoteEntries.Add(note);
                SelectedNoteId = note.Id;
            }
        }

        public int ChangedFileCount { get; set; }

        public string SelectedPaneId { get; set; }

        public string SelectedDiffPath { get; set; }

        public DiffReviewSourceKind DiffReviewSource { get; set; } = DiffReviewSourceKind.Live;

        public string SelectedCheckpointId { get; set; }

        internal GitThreadSnapshot BaselineSnapshot { get; set; }

        internal GitThreadSnapshot LiveSnapshot { get; set; }

        internal DateTimeOffset LiveSnapshotCapturedAt { get; set; }

        public string SelectedTabId
        {
            get => SelectedPaneId;
            set => SelectedPaneId = value;
        }

        public WorkspaceLayoutPreset LayoutPreset { get; set; }

        public int PaneLimit => Math.Clamp(SelfContainedDeployment.SampleConfig.MaxPaneCountPerThread, 2, 4);

        public int VisiblePaneCapacity => Math.Min(PaneLimit, Math.Clamp((int)LayoutPreset, 1, 4));

        public double PrimarySplitRatio { get; set; } = 0.58;

        public double SecondarySplitRatio { get; set; } = 0.5;

        public bool AutoFitPaneContentLocked { get; set; }

        public string ZoomedPaneId { get; set; }

        public List<WorkspacePaneRecord> Panes { get; } = new();

        internal List<WorkspaceDiffCheckpoint> DiffCheckpoints { get; } = new();

        public string PaneSummary => Panes.Count == 1 ? "1 pane" : $"{Panes.Count} panes";

        public string TabSummary => PaneSummary;

        private WorkspaceThreadNote ResolveSelectedNote()
        {
            return NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, SelectedNoteId, StringComparison.Ordinal))
                ?? NoteEntries.FirstOrDefault(candidate => !candidate.IsArchived)
                ?? NoteEntries.FirstOrDefault();
        }

        private void NotifyPaneRoots()
        {
            string effectiveWorktreePath = _worktreePath ?? Project?.RootPath;
            foreach (EditorPaneRecord editorPane in Panes.OfType<EditorPaneRecord>())
            {
                editorPane.Editor?.UpdateProjectRootPath(effectiveWorktreePath);
            }
        }
    }

    public abstract class WorkspacePaneRecord
    {
        protected WorkspacePaneRecord(string title, WorkspacePaneKind kind, string id = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            Title = title;
            Kind = kind;
        }

        public string Id { get; }

        public string Title { get; set; }

        public bool HasCustomTitle { get; set; }

        public string ReplayTool { get; set; }

        public string ReplaySessionId { get; set; }

        public string ReplayCommand { get; set; }

        public string ReplayArguments { get; set; }

        public bool RestoredFromSession { get; set; }

        public bool ReplayRestorePending { get; set; }

        public bool ReplayRestoreFailed { get; set; }

        public bool RequiresAttention { get; set; }

        public bool PersistExitedState { get; set; }

        public WorkspacePaneKind Kind { get; }

        public virtual bool IsExited { get; protected set; }

        public virtual bool IsDeferred => false;

        public abstract FrameworkElement View { get; }

        public abstract void ApplyTheme(ElementTheme theme);

        public abstract void FocusPane();

        public abstract void RequestLayout();

        public abstract void DisposePane();
    }

    internal sealed class DeferredPaneRecord : WorkspacePaneRecord
    {
        private FrameworkElement _placeholderView;

        internal DeferredPaneRecord(PaneSessionSnapshot snapshot)
            : base(ResolveTitle(snapshot), ParseKind(snapshot?.Kind), snapshot?.Id)
        {
            Snapshot = CloneSnapshot(snapshot);
            HasCustomTitle = snapshot?.HasCustomTitle == true;
            ReplayTool = snapshot?.ReplayTool;
            ReplaySessionId = snapshot?.ReplaySessionId;
            ReplayCommand = snapshot?.ReplayCommand;
            ReplayArguments = snapshot?.ReplayArguments;
            ReplayRestoreFailed = snapshot?.ReplayRestoreFailed == true;
            PersistExitedState = snapshot?.IsExited == true && snapshot?.ReplayRestoreFailed == true;
            IsExited = snapshot?.IsExited == true;
        }

        internal PaneSessionSnapshot Snapshot { get; }

        public string BrowserUri => Snapshot?.BrowserUri;

        public string SelectedBrowserTabId => Snapshot?.SelectedBrowserTabId;

        internal IReadOnlyList<BrowserTabSessionSnapshot> BrowserTabs => Snapshot?.BrowserTabs is { } tabs
            ? tabs
            : (IReadOnlyList<BrowserTabSessionSnapshot>)Array.Empty<BrowserTabSessionSnapshot>();

        public string DiffPath => Snapshot?.DiffPath;

        public string EditorFilePath => Snapshot?.EditorFilePath;

        public override bool IsDeferred => true;

        public override FrameworkElement View => _placeholderView ??= BuildPlaceholderView();

        public override void ApplyTheme(ElementTheme theme)
        {
        }

        public override void FocusPane()
        {
        }

        public override void RequestLayout()
        {
        }

        public override void DisposePane()
        {
        }

        private FrameworkElement BuildPlaceholderView()
        {
            string kindLabel = Kind switch
            {
                WorkspacePaneKind.Browser => "Web",
                WorkspacePaneKind.Editor => "Edit",
                WorkspacePaneKind.Diff => "Diff",
                _ => "Term",
            };

            StackPanel content = new()
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(new TextBlock
            {
                Text = kindLabel,
                FontSize = 11,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(Title) ? $"{kindLabel} pane" : Title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240,
            });
            content.Children.Add(new TextBlock
            {
                Text = "Restoring pane…",
                FontSize = 10,
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            return new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x08, 0x7E, 0x86, 0x91)),
                Children =
                {
                    content,
                },
            };
        }

        private static WorkspacePaneKind ParseKind(string kind)
        {
            return kind?.Trim().ToLowerInvariant() switch
            {
                "browser" => WorkspacePaneKind.Browser,
                "editor" => WorkspacePaneKind.Editor,
                "diff" => WorkspacePaneKind.Diff,
                _ => WorkspacePaneKind.Terminal,
            };
        }

        private static string ResolveTitle(PaneSessionSnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot?.Title))
            {
                return snapshot.Title.Trim();
            }

            return ParseKind(snapshot?.Kind) switch
            {
                WorkspacePaneKind.Browser => "Preview",
                WorkspacePaneKind.Editor => "Editor",
                WorkspacePaneKind.Diff => "Patch view",
                _ => "Terminal",
            };
        }

        private static PaneSessionSnapshot CloneSnapshot(PaneSessionSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            return new PaneSessionSnapshot
            {
                Id = snapshot.Id,
                Kind = snapshot.Kind,
                Title = snapshot.Title,
                HasCustomTitle = snapshot.HasCustomTitle,
                IsExited = snapshot.IsExited,
                ReplayRestoreFailed = snapshot.ReplayRestoreFailed,
                BrowserUri = snapshot.BrowserUri,
                SelectedBrowserTabId = snapshot.SelectedBrowserTabId,
                BrowserTabs = (snapshot.BrowserTabs ?? new List<BrowserTabSessionSnapshot>())
                    .Select(tab => new BrowserTabSessionSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    })
                    .ToList(),
                DiffPath = snapshot.DiffPath,
                EditorFilePath = snapshot.EditorFilePath,
                ReplayTool = snapshot.ReplayTool,
                ReplaySessionId = snapshot.ReplaySessionId,
                ReplayCommand = snapshot.ReplayCommand,
                ReplayArguments = snapshot.ReplayArguments,
            };
        }
    }

    public sealed class TerminalPaneRecord : WorkspacePaneRecord
    {
        public TerminalPaneRecord(string title, TerminalControl terminal, WorkspacePaneKind kind = WorkspacePaneKind.Terminal, string id = null)
            : base(title, kind, id)
        {
            Terminal = terminal;
        }

        public TerminalControl Terminal { get; }

        public override FrameworkElement View => Terminal;

        public override void ApplyTheme(ElementTheme theme) => Terminal.ApplyTheme(theme);

        public override void FocusPane() => Terminal.FocusTerminal();

        public override void RequestLayout() => Terminal.RequestFit();

        public override void DisposePane() => Terminal.DisposeTerminal();

        public void MarkExited()
        {
            IsExited = true;
        }

        public void MarkReplayRestorePending()
        {
            ReplayRestorePending = true;
            ReplayRestoreFailed = false;
            PersistExitedState = false;
        }

        public void MarkReplayRestoreSucceeded()
        {
            ReplayRestorePending = false;
            ReplayRestoreFailed = false;
            PersistExitedState = false;
        }

        public void MarkReplayRestoreFailed()
        {
            ReplayRestorePending = false;
            ReplayRestoreFailed = true;
            PersistExitedState = true;
        }
    }

    public sealed class BrowserPaneRecord : WorkspacePaneRecord
    {
        public BrowserPaneRecord(string title, BrowserPaneControl browser, string id = null)
            : base(title, WorkspacePaneKind.Browser, id)
        {
            Browser = browser;
        }

        public BrowserPaneControl Browser { get; }

        public override FrameworkElement View => Browser;

        public override void ApplyTheme(ElementTheme theme) => Browser.ApplyTheme(theme);

        public override void FocusPane() => Browser.FocusPane();

        public override void RequestLayout() => Browser.RequestLayout();

        public override void DisposePane() => Browser.DisposePane();
    }

    public sealed class EditorPaneRecord : WorkspacePaneRecord
    {
        public EditorPaneRecord(string title, EditorPaneControl editor, string id = null)
            : base(title, WorkspacePaneKind.Editor, id)
        {
            Editor = editor;
        }

        public EditorPaneControl Editor { get; }

        public override FrameworkElement View => Editor;

        public override void ApplyTheme(ElementTheme theme) => Editor.ApplyTheme(theme);

        public override void FocusPane() => Editor.FocusPane();

        public override void RequestLayout() => Editor.RequestLayout();

        public override void DisposePane() => Editor.DisposePane();
    }

    public sealed class DiffPaneRecord : WorkspacePaneRecord
    {
        public DiffPaneRecord(string title, DiffPaneHostControl diffPane, string diffPath = null, string id = null)
            : base(title, WorkspacePaneKind.Diff, id)
        {
            DiffPane = diffPane;
            DiffPath = diffPath;
        }

        public DiffPaneHostControl DiffPane { get; }

        public string DiffPath { get; set; }

        public override FrameworkElement View => DiffPane;

        public override void ApplyTheme(ElementTheme theme) => DiffPane.ApplyTheme(theme);

        public override void FocusPane() => DiffPane.FocusPane();

        public override void RequestLayout() => DiffPane.RequestLayout();

        public override void DisposePane() => DiffPane.DisposePane();
    }
}
