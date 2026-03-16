using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.UI;

namespace SelfContainedDeployment.Panes
{
    internal sealed class EditorPaneRenderSnapshot
    {
        public string SelectedPath { get; set; }

        public string Status { get; set; }

        public bool Dirty { get; set; }

        public bool ReadOnly { get; set; }

        public int FileCount { get; set; }

        public List<string> Files { get; set; } = new();

        public string Text { get; set; }
    }

    internal sealed class EditorPaneFileEntry
    {
        public string FullPath { get; init; }

        public string RelativePath { get; init; }
    }

    public sealed partial class EditorPaneControl : UserControl
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            "node_modules",
            "bin",
            "obj",
            "dist",
            "coverage",
            ".next",
            ".nuxt",
        };

        private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".7z",
            ".a",
            ".avi",
            ".bmp",
            ".class",
            ".db",
            ".dll",
            ".doc",
            ".docx",
            ".exe",
            ".gif",
            ".gz",
            ".ico",
            ".jar",
            ".jpeg",
            ".jpg",
            ".mp3",
            ".mp4",
            ".pdf",
            ".pdb",
            ".png",
            ".pyc",
            ".so",
            ".tar",
            ".ttf",
            ".wav",
            ".webm",
            ".woff",
            ".woff2",
            ".zip",
        };

        private static readonly TimeSpan ProjectFileCacheLifetime = TimeSpan.FromSeconds(3);
        private static readonly object ProjectFileCacheGate = new();
        private static readonly Dictionary<string, CachedProjectFileSnapshot> ProjectFileCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed class EditorWebMessage
        {
            public string Type { get; set; }

            public string Text { get; set; }

            public string RequestId { get; set; }
        }

        private sealed class EditorDocumentPayload
        {
            public string RequestId { get; set; }

            public string Mode { get; set; }

            public string Path { get; set; }

            public string Text { get; set; }

            public string OriginalText { get; set; }

            public string ModifiedText { get; set; }

            public string Language { get; set; }

            public string Theme { get; set; }

            public string LineEnding { get; set; }

            public bool ReadOnly { get; set; }

            public string LineNumbers { get; set; }

            public string RenderLineHighlight { get; set; }

            public string EmptyTitle { get; set; }

            public string EmptyBody { get; set; }
        }

        private sealed class CachedProjectFileSnapshot
        {
            public DateTimeOffset CapturedAt { get; init; }

            public List<EditorPaneFileEntry> Files { get; init; } = new();
        }

        private readonly List<EditorPaneFileEntry> _files = new();
        private bool _fileListLoaded;
        private const string DiffEmptyTitle = "No patch selected";
        private const string DiffEmptyBody = "Select a changed file from the review list.";
        private const double MinFitWidthZoomFactor = 0.62;
        private const double EditorCharacterWidth = 7.45;
        private const double CompareCharacterWidth = 7.2;
        private Task _initializationTask;
        private bool _loaded;
        private bool _webViewInitialized;
        private bool _editorReady;
        private bool _disposed;
        private string _paneId;
        private string _selectedFullPath;
        private string _selectedRelativePath;
        private string _loadedText = string.Empty;
        private string _currentText = string.Empty;
        private string _compareOriginalText = string.Empty;
        private string _compareModifiedText = string.Empty;
        private string _compareRawDiffText = string.Empty;
        private string _lineEnding = "\n";
        private string _statusText = "Preparing the editor.";
        private string _diffSummary = "Patch view";
        private Encoding _loadedEncoding = new UTF8Encoding(false);
        private ElementTheme _themePreference = ElementTheme.Default;
        private bool _dirty;
        private bool _readOnly;
        private readonly DispatcherQueueTimer _layoutTimer;
        private bool _forceLayoutPending;
        private double _lastLayoutWidth = -1;
        private double _lastLayoutHeight = -1;
        private string _lastAppliedDocumentRequestId;
        private bool _showCompactHeader = true;
        private bool _autoFitWidthLocked;
        private bool _fitWidthRequested;
        private double _lastAppliedZoomFactor = double.NaN;
        private int _maxVisibleLineLength;

        public EditorPaneControl()
        {
            InitializeComponent();
            PointerPressed += (_, _) => RaiseInteractionRequested();
            GotFocus += (_, _) => RaiseInteractionRequested();
            EditorView.GotFocus += (_, _) => RaiseInteractionRequested();
            ActualThemeChanged += OnActualThemeChanged;
            SizeChanged += OnEditorSizeChanged;
            _layoutTimer = DispatcherQueue.CreateTimer();
            _layoutTimer.IsRepeating = false;
            _layoutTimer.Interval = TimeSpan.FromMilliseconds(45);
            _layoutTimer.Tick += OnLayoutTimerTick;
            UpdateCompactHeader();
            ApplyEditorSurfaceTheme();
        }

        public event EventHandler<string> TitleChanged;

        public event EventHandler InteractionRequested;

        public event EventHandler StateChanged;

        public string ProjectRootPath { get; set; }

        public string InitialFilePath { get; set; }

        public string SelectedFilePath => _selectedRelativePath;

        public string SelectedFileFullPath => _selectedFullPath;

        public bool IsDirty => _dirty;

        public bool IsReadOnly => _readOnly;

        public bool CanSave => !string.IsNullOrWhiteSpace(_selectedFullPath) && !_readOnly && _dirty;

        public bool CanReload => !string.IsNullOrWhiteSpace(_selectedFullPath);

        public string StatusText => _statusText;

        public int FileCount => _files.Count;

        public bool DiffModeEnabled { get; set; }

        public bool ShowCompactHeader
        {
            get => _showCompactHeader;
            set
            {
                if (_showCompactHeader == value)
                {
                    return;
                }

                _showCompactHeader = value;
                UpdateCompactHeader();
            }
        }

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            RequestedTheme = theme;
            UpdateCompactHeader();
            ApplyEditorSurfaceTheme();
            _ = ApplyEditorShellThemeAsync();
            _ = ApplyEditorThemeAsync();
        }

        public void FocusPane()
        {
            EditorView.Focus(FocusState.Programmatic);
            _ = ExecuteEditorScriptAsync("window.__winmuxEditorHost?.focus?.();");
        }

        public void RequestLayout()
        {
            QueueEditorLayout(force: true);
        }

        public void ApplyFitToWidth(bool autoLock)
        {
            _autoFitWidthLocked = autoLock;
            _fitWidthRequested = true;
            QueueEditorLayout(force: true);
        }

        public void SetAutoFitWidth(bool enabled)
        {
            _autoFitWidthLocked = enabled;
            if (enabled)
            {
                _fitWidthRequested = true;
            }
        }

        public void DisposePane()
        {
            _disposed = true;
            _layoutTimer.Stop();

            if (EditorView.CoreWebView2 is not null)
            {
                EditorView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                EditorView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }

        internal async Task OpenFilePathAsync(string path)
        {
            if (DiffModeEnabled)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(true);
            await EnsureFileListLoadedAsync().ConfigureAwait(true);
            await OpenFileAsync(path).ConfigureAwait(true);
        }

        internal async Task ReloadCurrentFileAsync()
        {
            if (DiffModeEnabled)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(true);
            await EnsureFileListLoadedAsync().ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(_selectedFullPath))
            {
                await OpenInitialFileAsync().ConfigureAwait(true);
                return;
            }

            await OpenFileAsync(_selectedFullPath, savePendingChanges: false).ConfigureAwait(true);
        }

        internal async Task SaveCurrentFilePublicAsync()
        {
            await SaveCurrentFileAsync(silent: false).ConfigureAwait(true);
        }

        internal void SetCompareDocument(string path, string originalText, string modifiedText, string diffText, string summary = null)
        {
            DiffModeEnabled = true;
            _selectedFullPath = null;
            _selectedRelativePath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/');
            _compareOriginalText = originalText ?? string.Empty;
            _compareModifiedText = modifiedText ?? string.Empty;
            _compareRawDiffText = diffText ?? string.Empty;
            _loadedText = _compareModifiedText;
            _currentText = _compareModifiedText;
            _loadedEncoding = new UTF8Encoding(false);
            _lineEnding = DetectLineEnding(_compareModifiedText);
            _dirty = false;
            _readOnly = true;
            _diffSummary = string.IsNullOrWhiteSpace(summary)
                ? (string.IsNullOrWhiteSpace(_selectedRelativePath) ? "Patch review" : "Patch view")
                : summary;
            UpdateContentMetrics();
            SetStatusText(string.IsNullOrWhiteSpace(_selectedRelativePath)
                ? _diffSummary
                : $"Reviewing {_selectedRelativePath}");
            UpdateTitle();

            if (!HasCompareContent() && string.IsNullOrWhiteSpace(_compareRawDiffText))
            {
                ShowOverlay(DiffEmptyTitle, DiffEmptyBody);
            }
            else if (_editorReady && HasCompareContent())
            {
                HideOverlay();
            }
            else
            {
                ShowOverlay("Loading patch", "Preparing the review surface...");
            }

            _ = PushDocumentToEditorAsync();
            RaiseStateChanged();
        }

        internal static List<EditorPaneFileEntry> EnumerateProjectFilesForRoot(string rootPath, bool bypassCache = false)
        {
            return EnumerateProjectFiles(rootPath, bypassCache);
        }

        internal EditorPaneRenderSnapshot GetRenderSnapshot(int maxChars = 0, int maxFiles = 0)
        {
            IEnumerable<string> fileList = _files.Select(file => file.RelativePath);
            if (maxFiles > 0)
            {
                fileList = fileList.Take(maxFiles);
            }

            string text = _currentText ?? string.Empty;
            if (maxChars > 0 && text.Length > maxChars)
            {
                text = text[..maxChars];
            }

            return new EditorPaneRenderSnapshot
            {
                SelectedPath = _selectedRelativePath ?? string.Empty,
                Status = _statusText ?? string.Empty,
                Dirty = _dirty,
                ReadOnly = _readOnly,
                FileCount = _files.Count,
                Files = fileList.ToList(),
                Text = text,
            };
        }

        internal DiffPaneRenderSnapshot GetDiffRenderSnapshot(int maxLines = 0)
        {
            string rawText = _compareRawDiffText ?? string.Empty;
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
                Path = _selectedRelativePath ?? string.Empty,
                Summary = _diffSummary ?? string.Empty,
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
                        Foreground = SerializeBrush(ResolveDiffBrush(kind)),
                    };
                }).ToList(),
            };
        }

        public void ApplyAutomationIdentity(string paneId, string automationPrefix = "shell-editor-pane", string viewName = "Code editor")
        {
            if (string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            _paneId = paneId;
            AutomationProperties.SetAutomationId(this, $"{automationPrefix}-{paneId}");
            AutomationProperties.SetName(this, viewName);
            AutomationProperties.SetAutomationId(EditorRoot, $"{automationPrefix}-root-{paneId}");
            AutomationProperties.SetAutomationId(EditorHeader, $"{automationPrefix}-header-{paneId}");
            AutomationProperties.SetName(EditorHeader, $"{viewName} header");
            AutomationProperties.SetAutomationId(EditorHeaderPathText, $"{automationPrefix}-header-path-{paneId}");
            AutomationProperties.SetAutomationId(EditorHeaderMetaText, $"{automationPrefix}-header-meta-{paneId}");
            AutomationProperties.SetAutomationId(EditorView, $"{automationPrefix}-webview-{paneId}");
            AutomationProperties.SetName(EditorView, viewName);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            UpdateCompactHeader();
            ApplyEditorSurfaceTheme();
            try
            {
                await EnsureInitializedAsync().ConfigureAwait(true);
                if (DiffModeEnabled)
                {
                    return;
                }

                await EnsureFileListLoadedAsync().ConfigureAwait(true);
                await OpenInitialFileAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShowOverlay("Editor unavailable", ex.Message);
                SetStatusText($"Editor failed: {ex.Message}");
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _layoutTimer.Stop();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_themePreference != ElementTheme.Default)
            {
                return;
            }

            UpdateCompactHeader();
            ApplyEditorSurfaceTheme();
            _ = ApplyEditorShellThemeAsync();
            _ = ApplyEditorThemeAsync();
        }

        private async Task EnsureInitializedAsync()
        {
            if (_webViewInitialized || _disposed)
            {
                return;
            }

            Task initializationTask = _initializationTask;
            if (initializationTask is null)
            {
                initializationTask = InitializeEditorWebViewAsync();
                _initializationTask = initializationTask;
            }

            await initializationTask.ConfigureAwait(true);
        }

        private async Task InitializeEditorWebViewAsync()
        {
            CoreWebView2 core;
            try
            {
                CoreWebView2Environment environment = await TerminalControl.GetEnvironmentAsync().ConfigureAwait(true);
                await EditorView.EnsureCoreWebView2Async(environment).AsTask().ConfigureAwait(true);
                core = await WaitForCoreWebView2Async().ConfigureAwait(true);
            }
            catch
            {
                await EditorView.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
                core = await WaitForCoreWebView2Async().ConfigureAwait(true);
            }

            ConfigureInitializedEditor(core);
            Uri editorHostUri = BuildEditorHostUri();
            EditorView.Source = editorHostUri;
            _webViewInitialized = true;
            ShowOverlay("Loading editor", "Preparing the code surface...");
        }

        private void ConfigureInitializedEditor(CoreWebView2 core)
        {
            if (core is null)
            {
                throw new InvalidOperationException("Editor CoreWebView2 was null after initialization.");
            }

            CoreWebView2Settings settings = core.Settings;
            if (settings is not null)
            {
                settings.AreDefaultContextMenusEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.AreBrowserAcceleratorKeysEnabled = true;
            }

            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
        }

        private async Task<CoreWebView2> WaitForCoreWebView2Async()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (EditorView.CoreWebView2 is not null)
                {
                    return EditorView.CoreWebView2;
                }

                await Task.Delay(50).ConfigureAwait(true);
            }

            throw new InvalidOperationException("Editor WebView2 core was not created.");
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                ShowOverlay("Editor unavailable", "The code editor surface did not load.");
                SetStatusText("Editor surface failed to load.");
                return;
            }

            ApplyEditorSurfaceTheme();
            _ = ApplyEditorShellThemeAsync();
            _ = ApplyEditorThemeAsync();
        }

        private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            EditorWebMessage message = DeserializeWebMessage(args);
            if (message is null)
            {
                return;
            }

            switch (message.Type?.Trim().ToLowerInvariant())
            {
                case "ready":
                    _editorReady = true;
                    await PushDocumentToEditorAsync().ConfigureAwait(true);
                    if (DiffModeEnabled)
                    {
                        if (!HasCompareContent())
                        {
                            ShowOverlay(DiffEmptyTitle, DiffEmptyBody);
                        }
                        else
                        {
                            HideOverlay();
                        }
                    }
                    else
                    {
                        HideOverlay();
                    }

                    QueueEditorLayout(force: true);
                    break;
                case "contentchanged":
                    _currentText = message.Text ?? string.Empty;
                    _dirty = !string.Equals(_currentText, _loadedText, StringComparison.Ordinal);
                    int previousMaxVisibleLineLength = _maxVisibleLineLength;
                    UpdateContentMetrics();
                    SetStatusText(_dirty
                        ? $"{_selectedRelativePath} - unsaved changes"
                        : string.IsNullOrWhiteSpace(_selectedRelativePath)
                            ? "Select a file to start editing."
                            : $"Editing {_selectedRelativePath}");
                    if (_autoFitWidthLocked && previousMaxVisibleLineLength != _maxVisibleLineLength)
                    {
                        QueueEditorLayout(force: true);
                    }
                    RaiseStateChanged();
                    break;
                case "documentapplied":
                    if (!string.IsNullOrWhiteSpace(message.RequestId))
                    {
                        _lastAppliedDocumentRequestId = message.RequestId;
                    }

                    if (_fitWidthRequested || _autoFitWidthLocked)
                    {
                        QueueEditorLayout(force: true);
                    }

                    break;
                case "saverequested":
                    await SaveCurrentFileAsync(silent: false).ConfigureAwait(true);
                    break;
            }
        }

        private static EditorWebMessage DeserializeWebMessage(CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = args.WebMessageAsJson;
                return JsonSerializer.Deserialize<EditorWebMessage>(json, JsonOptions);
            }
            catch
            {
                try
                {
                    string text = args.TryGetWebMessageAsString();
                    return JsonSerializer.Deserialize<EditorWebMessage>(text, JsonOptions);
                }
                catch
                {
                    return null;
                }
            }
        }

        private async Task EnsureFileListLoadedAsync()
        {
            if (_fileListLoaded)
            {
                return;
            }

            string rootPath = NormalizeRootPath(ProjectRootPath);
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                SetStatusText("Project path is unavailable.");
                ShowOverlay("Editor unavailable", "This thread does not have a readable project root.");
                RaiseStateChanged();
                return;
            }

            List<EditorPaneFileEntry> files = await Task.Run(() => EnumerateProjectFiles(rootPath)).ConfigureAwait(true);
            _files.Clear();
            _files.AddRange(files);
            _fileListLoaded = true;
            RaiseStateChanged();
        }

        private async Task OpenInitialFileAsync()
        {
            string targetPath = ResolveInitialPath();
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                await OpenFileAsync(targetPath, savePendingChanges: false).ConfigureAwait(true);
                return;
            }

            if (_files.Count == 0)
            {
                _selectedFullPath = null;
                _selectedRelativePath = null;
                _currentText = string.Empty;
                _loadedText = string.Empty;
                _dirty = false;
                _readOnly = false;
                UpdateContentMetrics();
                SetStatusText("No editable files were found in this project.");
                ShowOverlay("No file selected", "Use the Files inspector tab to open a file.");
                await PushDocumentToEditorAsync().ConfigureAwait(true);
                return;
            }

            await OpenFileAsync(_files[0].FullPath, savePendingChanges: false).ConfigureAwait(true);
        }

        private string ResolveInitialPath()
        {
            string rootPath = NormalizeRootPath(ProjectRootPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }

            string candidate = ResolveExistingFilePath(rootPath, InitialFilePath);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            string readmePath = Path.Combine(rootPath, "README.md");
            if (File.Exists(readmePath))
            {
                return readmePath;
            }

            return null;
        }

        private async Task OpenFileAsync(string path, bool savePendingChanges = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (savePendingChanges && !await SaveCurrentFileAsync(silent: false, skipIfClean: true).ConfigureAwait(true))
            {
                return;
            }

            RaiseInteractionRequested();

            string rootPath = NormalizeRootPath(ProjectRootPath);
            string fullPath = ResolveExistingFilePath(rootPath, path);
            string relativePath = MakeRelativePath(rootPath, fullPath);

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(true);
                if (LooksBinary(bytes))
                {
                    _selectedFullPath = fullPath;
                    _selectedRelativePath = relativePath;
                    _loadedText = string.Empty;
                    _currentText = string.Empty;
                    _loadedEncoding = new UTF8Encoding(false);
                    _lineEnding = "\n";
                    _dirty = false;
                    _readOnly = true;
                    UpdateContentMetrics();
                    SetStatusText("This file looks binary and cannot be edited here.");
                    UpdateTitle();
                    ShowOverlay("File unavailable", "This file looks binary and cannot be edited in the GUI editor.");
                    await PushDocumentToEditorAsync().ConfigureAwait(true);
                    RaiseStateChanged();
                    return;
                }

                (string text, Encoding encoding) = DecodeFile(bytes);
                _selectedFullPath = fullPath;
                _selectedRelativePath = relativePath;
                _loadedEncoding = encoding;
                _lineEnding = DetectLineEnding(text);
                _loadedText = text;
                _currentText = text;
                _dirty = false;
                _readOnly = false;
                UpdateContentMetrics();
                SetStatusText($"Editing {relativePath}");
                UpdateTitle();
                HideOverlay();
                await PushDocumentToEditorAsync().ConfigureAwait(true);
                RaiseStateChanged();
            }
            catch (Exception ex)
            {
                _selectedFullPath = fullPath;
                _selectedRelativePath = relativePath;
                _loadedText = string.Empty;
                _currentText = string.Empty;
                _loadedEncoding = new UTF8Encoding(false);
                _lineEnding = "\n";
                _dirty = false;
                _readOnly = true;
                UpdateContentMetrics();
                SetStatusText($"Could not load file: {ex.Message}");
                UpdateTitle();
                ShowOverlay("File unavailable", ex.Message);
                await PushDocumentToEditorAsync().ConfigureAwait(true);
                RaiseStateChanged();
            }
        }

        private async Task<bool> SaveCurrentFileAsync(bool silent, bool skipIfClean = false)
        {
            if (DiffModeEnabled || string.IsNullOrWhiteSpace(_selectedFullPath) || _readOnly)
            {
                return true;
            }

            if (skipIfClean && !_dirty)
            {
                return true;
            }

            if (!_dirty)
            {
                if (!silent)
                {
                    SetStatusText("No changes to save.");
                }

                return true;
            }

            try
            {
                string textToWrite = NormalizeOutgoingText(_currentText ?? string.Empty, _lineEnding);
                Encoding encoding = _loadedEncoding ?? new UTF8Encoding(false);
                await using FileStream stream = new(_selectedFullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                await using StreamWriter writer = new(stream, encoding);
                await writer.WriteAsync(textToWrite).ConfigureAwait(true);
                await writer.FlushAsync().ConfigureAwait(true);

                _loadedText = textToWrite;
                _currentText = textToWrite;
                _dirty = false;
                UpdateContentMetrics();
                SetStatusText($"Saved {_selectedRelativePath}");
                await PushDocumentToEditorAsync().ConfigureAwait(true);
                RaiseStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                SetStatusText($"Save failed: {ex.Message}");
                RaiseStateChanged();
                return false;
            }
        }

        private async Task PushDocumentToEditorAsync()
        {
            if (!_editorReady)
            {
                return;
            }

            string requestId = Guid.NewGuid().ToString("N");
            EditorDocumentPayload payload = new()
            {
                RequestId = requestId,
                Mode = DiffModeEnabled ? "compare" : "file",
                Path = _selectedRelativePath ?? string.Empty,
                Text = DiffModeEnabled ? (_compareModifiedText ?? string.Empty) : (_currentText ?? string.Empty),
                OriginalText = DiffModeEnabled ? (_compareOriginalText ?? string.Empty) : string.Empty,
                ModifiedText = DiffModeEnabled ? (_compareModifiedText ?? string.Empty) : string.Empty,
                Language = ResolveLanguage(_selectedRelativePath),
                Theme = ResolveCurrentThemeName(),
                LineEnding = _lineEnding,
                ReadOnly = DiffModeEnabled || _readOnly || string.IsNullOrWhiteSpace(_selectedFullPath),
                LineNumbers = "on",
                RenderLineHighlight = DiffModeEnabled ? "line" : "gutter",
                EmptyTitle = DiffModeEnabled ? DiffEmptyTitle : "No file selected",
                EmptyBody = DiffModeEnabled ? DiffEmptyBody : "Use the Files tab in the inspector to open a file.",
            };

            PostEditorMessage("document", payload);
            await Task.Delay(140).ConfigureAwait(true);
            if (!string.Equals(_lastAppliedDocumentRequestId, requestId, StringComparison.Ordinal))
            {
                string json = JsonSerializer.Serialize(payload, JsonOptions);
                string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                await ExecuteEditorScriptAsync($"window.__winmuxEditorHost?.setDocument?.(JSON.parse(atob('{base64}')));").ConfigureAwait(true);
            }
        }

        private async Task ApplyEditorThemeAsync()
        {
            if (!_editorReady)
            {
                return;
            }

            string theme = ResolveCurrentThemeName();
            await ExecuteEditorScriptAsync($"window.__winmuxEditorHost?.setTheme?.('{theme}');").ConfigureAwait(true);
        }

        private async Task ApplyEditorShellThemeAsync()
        {
            string theme = ResolveCurrentThemeName();
            await ExecuteEditorScriptAsync(
                $"(() => {{ document.documentElement.dataset.theme = '{theme}'; if (document.body) document.body.dataset.theme = '{theme}'; }})()")
                .ConfigureAwait(true);
        }

        private void ApplyEditorSurfaceTheme()
        {
            Color backgroundColor = ResolveCurrentThemeName() == "light"
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x11, 0x12, 0x14);

            if (EditorView is not null)
            {
                EditorView.DefaultBackgroundColor = backgroundColor;
            }
        }

        private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            {
                return;
            }

            QueueEditorLayout(force: false);
        }

        private void QueueEditorLayout(bool force)
        {
            _forceLayoutPending |= force;
            _layoutTimer.Stop();
            _layoutTimer.Start();
        }

        private async void OnLayoutTimerTick(DispatcherQueueTimer sender, object args)
        {
            _layoutTimer.Stop();

            double width = Math.Round(ActualWidth);
            double height = Math.Round(ActualHeight);
            if (width <= 1 || height <= 1)
            {
                return;
            }

            if (!_forceLayoutPending &&
                Math.Abs(_lastLayoutWidth - width) < 1 &&
                Math.Abs(_lastLayoutHeight - height) < 1)
            {
                return;
            }

            _lastLayoutWidth = width;
            _lastLayoutHeight = height;
            _forceLayoutPending = false;
            await ExecuteEditorScriptAsync("window.__winmuxEditorHost?.layout?.();").ConfigureAwait(true);
            await ApplyFitWidthAsync().ConfigureAwait(true);
        }

        private async Task ExecuteEditorScriptAsync(string script)
        {
            if (_disposed || !_webViewInitialized || EditorView.CoreWebView2 is null || string.IsNullOrWhiteSpace(script))
            {
                return;
            }

            try
            {
                await EditorView.CoreWebView2.ExecuteScriptAsync(script).AsTask().ConfigureAwait(true);
            }
            catch
            {
            }
        }

        private void PostEditorMessage<TPayload>(string type, TPayload payload)
        {
            if (_disposed || !_webViewInitialized || EditorView.CoreWebView2 is null || string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    type,
                    payload,
                }, JsonOptions);
                EditorView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch
            {
            }
        }

        private void UpdateTitle()
        {
            string title = string.IsNullOrWhiteSpace(_selectedRelativePath)
                ? (DiffModeEnabled ? "Patch review" : "Editor")
                : Path.GetFileName(_selectedRelativePath);
            TitleChanged?.Invoke(this, title);
        }

        private void ShowOverlay(string title, string message)
        {
            EditorOverlayTitle.Text = title;
            EditorOverlayText.Text = message;
            EditorOverlay.Visibility = Visibility.Visible;
        }

        private void HideOverlay()
        {
            EditorOverlay.Visibility = Visibility.Collapsed;
        }

        private void SetStatusText(string status)
        {
            _statusText = status ?? string.Empty;
            UpdateCompactHeader();
        }

        private void RaiseInteractionRequested()
        {
            InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool HasCompareContent()
        {
            return !string.IsNullOrWhiteSpace(_compareOriginalText) ||
                !string.IsNullOrWhiteSpace(_compareModifiedText);
        }

        private void UpdateCompactHeader()
        {
            if (EditorHeader is null)
            {
                return;
            }

            EditorHeader.Visibility = _showCompactHeader ? Visibility.Visible : Visibility.Collapsed;
            if (!_showCompactHeader)
            {
                return;
            }

            EditorHeaderPathText.Text = string.IsNullOrWhiteSpace(_selectedRelativePath)
                ? (DiffModeEnabled ? "Patch review" : "No file selected")
                : _selectedRelativePath.Replace('\\', '/');
            EditorHeaderMetaText.Text = ResolveHeaderMetaText();
        }

        private string ResolveHeaderMetaText()
        {
            if (DiffModeEnabled)
            {
                return string.IsNullOrWhiteSpace(_diffSummary) ? "Patch view" : _diffSummary;
            }

            if (_readOnly)
            {
                return "Read only";
            }

            if (_dirty)
            {
                return "Unsaved";
            }

            return string.IsNullOrWhiteSpace(_selectedRelativePath) ? "Waiting" : "Ready";
        }

        private void UpdateContentMetrics()
        {
            _maxVisibleLineLength = DiffModeEnabled
                ? Math.Max(
                    Math.Max(EstimateLongestLineLength(_compareOriginalText), EstimateLongestLineLength(_compareModifiedText)),
                    EstimateLongestLineLength(_compareRawDiffText))
                : EstimateLongestLineLength(_currentText);

            if (_autoFitWidthLocked)
            {
                _fitWidthRequested = true;
            }
        }

        private static int EstimateLongestLineLength(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int longest = 0;
            int current = 0;
            foreach (char ch in text)
            {
                if (ch == '\r')
                {
                    continue;
                }

                if (ch == '\n')
                {
                    if (current > longest)
                    {
                        longest = current;
                    }

                    current = 0;
                    continue;
                }

                current++;
            }

            return Math.Max(longest, current);
        }

        private async Task ApplyFitWidthAsync()
        {
            if ((!_fitWidthRequested && !_autoFitWidthLocked) || _disposed || !_webViewInitialized || EditorView.CoreWebView2 is null)
            {
                return;
            }

            double availableWidth = Math.Max(0, EditorRoot.ActualWidth - 24);
            if (_showCompactHeader && EditorHeader is not null)
            {
                availableWidth = Math.Max(0, availableWidth - 6);
            }

            if (availableWidth <= 1)
            {
                return;
            }

            double estimatedWidth = EstimateDesiredContentWidth();
            double zoomFactor = estimatedWidth <= 0
                ? 1.0
                : Math.Clamp(availableWidth / estimatedWidth, MinFitWidthZoomFactor, 1.0);
            zoomFactor = zoomFactor >= 0.98 ? 1.0 : zoomFactor;
            if (!double.IsNaN(_lastAppliedZoomFactor) && Math.Abs(_lastAppliedZoomFactor - zoomFactor) < 0.01)
            {
                _fitWidthRequested = false;
                return;
            }

            string zoomText = zoomFactor.ToString("0.###", CultureInfo.InvariantCulture);
            _lastAppliedZoomFactor = zoomFactor;
            _fitWidthRequested = false;
            await ExecuteEditorScriptAsync(
                $"(() => {{ document.documentElement.style.zoom = '{zoomText}'; if (document.body) document.body.style.zoom = '{zoomText}'; }})()")
                .ConfigureAwait(true);
        }

        private double EstimateDesiredContentWidth()
        {
            int maxLineLength = Math.Max(_maxVisibleLineLength, 24);
            double gutterWidth = DiffModeEnabled ? 128d : 96d;
            double paddingWidth = DiffModeEnabled ? 64d : 44d;
            double characterWidth = DiffModeEnabled ? CompareCharacterWidth : EditorCharacterWidth;
            return gutterWidth + paddingWidth + (maxLineLength * characterWidth);
        }

        private Brush ResolveDiffBrush(string kind)
        {
            string key = kind switch
            {
                "addition" => "ShellSuccessBrush",
                "deletion" => "ShellDangerBrush",
                "hunk" => "ShellInfoBrush",
                "metadata" => "ShellTextSecondaryBrush",
                "empty" => "ShellTextSecondaryBrush",
                _ => "ShellTextPrimaryBrush",
            };
            return (Brush)Application.Current.Resources[key];
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

        private static string SerializeBrush(Brush brush)
        {
            return brush switch
            {
                SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
                null => null,
                _ => brush.GetType().Name,
            };
        }

        private static string ResolveEditorHostPath()
        {
            string overrideRoot = Environment.GetEnvironmentVariable("NATIVE_TERMINAL_WEB_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                string candidate = Path.Combine(overrideRoot, "editor-host.html");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(AppContext.BaseDirectory, "Web", "editor-host.html");
        }

        private Uri BuildEditorHostUri()
        {
            Uri baseUri = new(ResolveEditorHostPath());
            string theme = ResolveCurrentThemeName();
            UriBuilder builder = new(baseUri)
            {
                Query = $"theme={theme}",
            };
            return builder.Uri;
        }

        private string ResolveCurrentThemeName()
        {
            ElementTheme resolvedTheme = _themePreference == ElementTheme.Default ? ActualTheme : _themePreference;
            if (resolvedTheme == ElementTheme.Default)
            {
                resolvedTheme = SelfContainedDeployment.MainPage.Current?.ActualTheme == ElementTheme.Light
                    ? ElementTheme.Light
                    : SelfContainedDeployment.SampleConfig.CurrentTheme == ElementTheme.Light
                        ? ElementTheme.Light
                        : ElementTheme.Dark;
            }

            return ResolveThemeName(resolvedTheme);
        }

        private static string ResolveThemeName(ElementTheme theme)
        {
            return theme switch
            {
                ElementTheme.Light => "light",
                _ => "dark",
            };
        }

        private static string ResolveLanguage(string relativePath)
        {
            string extension = Path.GetExtension(relativePath ?? string.Empty);
            return extension?.ToLowerInvariant() switch
            {
                ".cs" => "csharp",
                ".css" => "css",
                ".html" or ".xaml" => "xml",
                ".js" or ".cjs" or ".mjs" => "javascript",
                ".json" => "json",
                ".md" => "markdown",
                ".ps1" => "powershell",
                ".py" => "python",
                ".rs" => "rust",
                ".sh" => "shell",
                ".sql" => "sql",
                ".toml" => "ini",
                ".ts" or ".tsx" => "typescript",
                ".xml" => "xml",
                ".yaml" or ".yml" => "yaml",
                _ => "plaintext",
            };
        }

        private static string DetectLineEnding(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "\n";
            }

            return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        private static string NormalizeOutgoingText(string text, string lineEnding)
        {
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return string.Equals(lineEnding, "\r\n", StringComparison.Ordinal)
                ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal)
                : normalized;
        }

        private static List<EditorPaneFileEntry> EnumerateProjectFiles(string rootPath, bool bypassCache = false)
        {
            string normalizedRootPath = NormalizeRootPath(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedRootPath) || !Directory.Exists(normalizedRootPath))
            {
                return new List<EditorPaneFileEntry>();
            }

            if (!bypassCache)
            {
                lock (ProjectFileCacheGate)
                {
                    if (ProjectFileCache.TryGetValue(normalizedRootPath, out CachedProjectFileSnapshot cached) &&
                        DateTimeOffset.UtcNow - cached.CapturedAt <= ProjectFileCacheLifetime)
                    {
                        return CloneFileEntries(cached.Files);
                    }
                }
            }

            List<EditorPaneFileEntry> results = new();
            Stack<string> stack = new();
            stack.Push(normalizedRootPath);

            while (stack.Count > 0)
            {
                string current = stack.Pop();
                try
                {
                    foreach (string directory in Directory.EnumerateDirectories(current))
                    {
                        string name = Path.GetFileName(directory);
                        if (IgnoredDirectoryNames.Contains(name))
                        {
                            continue;
                        }

                        stack.Push(directory);
                    }

                    foreach (string file in Directory.EnumerateFiles(current))
                    {
                        if (!IsProbablyEditableFile(file))
                        {
                            continue;
                        }

                        results.Add(new EditorPaneFileEntry
                        {
                            FullPath = file,
                            RelativePath = MakeRelativePath(normalizedRootPath, file),
                        });
                    }
                }
                catch
                {
                }
            }

            List<EditorPaneFileEntry> ordered = results
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (ProjectFileCacheGate)
            {
                ProjectFileCache[normalizedRootPath] = new CachedProjectFileSnapshot
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    Files = CloneFileEntries(ordered),
                };
            }

            return ordered;
        }

        private static bool IsProbablyEditableFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (!string.IsNullOrWhiteSpace(extension) && IgnoredExtensions.Contains(extension))
            {
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(filePath);
            }
            catch
            {
                return false;
            }

            return info.Exists && info.Length <= 1024 * 1024;
        }

        private static bool LooksBinary(byte[] bytes)
        {
            int sampleLength = Math.Min(bytes.Length, 2048);
            for (int index = 0; index < sampleLength; index++)
            {
                if (bytes[index] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static (string Text, Encoding Encoding) DecodeFile(byte[] bytes)
        {
            using MemoryStream stream = new(bytes);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string text = reader.ReadToEnd();
            Encoding encoding = reader.CurrentEncoding ?? new UTF8Encoding(false);
            return (text, encoding);
        }

        private string ResolveExistingFilePath(string rootPath, string candidatePath)
        {
            string resolvedPath = ResolvePath(rootPath, candidatePath);
            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
            {
                return resolvedPath;
            }

            string normalizedCandidatePath = NormalizeComparablePath(candidatePath);
            if (!string.IsNullOrWhiteSpace(normalizedCandidatePath))
            {
                EditorPaneFileEntry match = _files.FirstOrDefault(file =>
                    string.Equals(file.RelativePath, normalizedCandidatePath, StringComparison.OrdinalIgnoreCase));
                if (match is not null && File.Exists(match.FullPath))
                {
                    return match.FullPath;
                }
            }

            return resolvedPath;
        }

        private static string ResolvePath(string rootPath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return null;
            }

            string normalizedCandidatePath = NormalizeRootPath(candidatePath);
            if (Path.IsPathRooted(candidatePath) || candidatePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedCandidatePath;
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return normalizedCandidatePath;
            }

            return NormalizeRootPath(Path.Combine(rootPath, candidatePath));
        }

        private static string MakeRelativePath(string rootPath, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return Path.GetFileName(fullPath);
            }

            try
            {
                string relative = Path.GetRelativePath(rootPath, fullPath);
                return relative.Replace('\\', '/');
            }
            catch
            {
                return fullPath.Replace('\\', '/');
            }
        }

        private static string NormalizeRootPath(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }

            try
            {
                string candidate = rootPath.Trim();
                if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri fileUri) && fileUri.IsFile)
                {
                    candidate = fileUri.LocalPath;
                }

                string normalizedPath = ShellProfiles.NormalizeProjectPath(candidate);
                if (ShellProfiles.TryResolveLocalStoragePath(normalizedPath, out string localPath))
                {
                    return Path.GetFullPath(localPath);
                }

                return normalizedPath;
            }
            catch
            {
                return null;
            }
        }

        private static List<EditorPaneFileEntry> CloneFileEntries(IEnumerable<EditorPaneFileEntry> files)
        {
            return files?
                .Select(file => new EditorPaneFileEntry
                {
                    FullPath = file.FullPath,
                    RelativePath = file.RelativePath,
                })
                .ToList()
                ?? new List<EditorPaneFileEntry>();
        }

        private static string NormalizeComparablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                if (Uri.TryCreate(path.Trim(), UriKind.Absolute, out Uri fileUri) && fileUri.IsFile)
                {
                    path = fileUri.LocalPath;
                }
            }
            catch
            {
            }

            return path.Trim().Replace('\\', '/');
        }
    }
}
