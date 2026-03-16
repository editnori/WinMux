using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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

        private sealed class EditorWebMessage
        {
            public string Type { get; set; }

            public string Text { get; set; }
        }

        private sealed class EditorDocumentPayload
        {
            public string Path { get; set; }

            public string Text { get; set; }

            public string Language { get; set; }

            public string Theme { get; set; }

            public string LineEnding { get; set; }

            public bool ReadOnly { get; set; }
        }

        private readonly List<EditorPaneFileEntry> _files = new();
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
        private string _lineEnding = "\n";
        private string _statusText = "Preparing the editor.";
        private Encoding _loadedEncoding = new UTF8Encoding(false);
        private ElementTheme _themePreference = ElementTheme.Default;
        private bool _dirty;
        private bool _readOnly;

        public EditorPaneControl()
        {
            InitializeComponent();
            PointerPressed += (_, _) => RaiseInteractionRequested();
            GotFocus += (_, _) => RaiseInteractionRequested();
            EditorView.GotFocus += (_, _) => RaiseInteractionRequested();
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

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            RequestedTheme = theme;
            _ = ApplyEditorThemeAsync();
        }

        public void FocusPane()
        {
            EditorView.Focus(FocusState.Programmatic);
            _ = ExecuteEditorScriptAsync("window.__winmuxEditorHost?.focus?.();");
        }

        public void RequestLayout()
        {
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();
            _ = ExecuteEditorScriptAsync("window.__winmuxEditorHost?.layout?.();");
        }

        public void DisposePane()
        {
            _disposed = true;

            if (EditorView.CoreWebView2 is not null)
            {
                EditorView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                EditorView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }

        internal async Task OpenFilePathAsync(string path)
        {
            await EnsureInitializedAsync().ConfigureAwait(true);
            await EnsureFileListLoadedAsync().ConfigureAwait(true);
            await OpenFileAsync(path).ConfigureAwait(true);
        }

        internal async Task ReloadCurrentFileAsync()
        {
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

        internal static List<EditorPaneFileEntry> EnumerateProjectFilesForRoot(string rootPath)
        {
            return EnumerateProjectFiles(rootPath);
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

        public void ApplyAutomationIdentity(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            _paneId = paneId;
            AutomationProperties.SetAutomationId(this, $"shell-editor-pane-{paneId}");
            AutomationProperties.SetName(this, "Editor pane");
            AutomationProperties.SetAutomationId(EditorRoot, $"shell-editor-pane-root-{paneId}");
            AutomationProperties.SetAutomationId(EditorView, $"shell-editor-pane-webview-{paneId}");
            AutomationProperties.SetName(EditorView, "Code editor");
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                await EnsureInitializedAsync().ConfigureAwait(true);
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
            DisposePane();
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
            string editorHostPath = ResolveEditorHostPath();
            EditorView.Source = new Uri(editorHostPath);
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
                    HideOverlay();
                    await PushDocumentToEditorAsync().ConfigureAwait(true);
                    break;
                case "contentchanged":
                    _currentText = message.Text ?? string.Empty;
                    _dirty = !string.Equals(_currentText, _loadedText, StringComparison.Ordinal);
                    SetStatusText(_dirty
                        ? $"{_selectedRelativePath} - unsaved changes"
                        : string.IsNullOrWhiteSpace(_selectedRelativePath)
                            ? "Select a file to start editing."
                            : $"Editing {_selectedRelativePath}");
                    RaiseStateChanged();
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
            if (_files.Count > 0)
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

            string candidate = ResolvePath(rootPath, InitialFilePath);
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
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
            string fullPath = ResolvePath(rootPath, path);
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
                SetStatusText($"Could not load file: {ex.Message}");
                UpdateTitle();
                ShowOverlay("File unavailable", ex.Message);
                await PushDocumentToEditorAsync().ConfigureAwait(true);
                RaiseStateChanged();
            }
        }

        private async Task<bool> SaveCurrentFileAsync(bool silent, bool skipIfClean = false)
        {
            if (string.IsNullOrWhiteSpace(_selectedFullPath) || _readOnly)
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

            EditorDocumentPayload payload = new()
            {
                Path = _selectedRelativePath ?? string.Empty,
                Text = _currentText ?? string.Empty,
                Language = ResolveLanguage(_selectedRelativePath),
                Theme = ResolveThemeName(_themePreference),
                LineEnding = _lineEnding,
                ReadOnly = _readOnly || string.IsNullOrWhiteSpace(_selectedFullPath),
            };

            string json = JsonSerializer.Serialize(payload, JsonOptions)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
            await ExecuteEditorScriptAsync($"window.__winmuxEditorHost?.setDocument?.(JSON.parse('{json}'));").ConfigureAwait(true);
        }

        private async Task ApplyEditorThemeAsync()
        {
            if (!_editorReady)
            {
                return;
            }

            string theme = ResolveThemeName(_themePreference);
            await ExecuteEditorScriptAsync($"window.__winmuxEditorHost?.setTheme?.('{theme}');").ConfigureAwait(true);
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

        private void UpdateTitle()
        {
            string title = string.IsNullOrWhiteSpace(_selectedRelativePath)
                ? "Editor"
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
        }

        private void RaiseInteractionRequested()
        {
            InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
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

        private static List<EditorPaneFileEntry> EnumerateProjectFiles(string rootPath)
        {
            List<EditorPaneFileEntry> results = new();
            Stack<string> stack = new();
            stack.Push(rootPath);

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
                            RelativePath = MakeRelativePath(rootPath, file),
                        });
                    }
                }
                catch
                {
                }
            }

            return results
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
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

        private static string ResolvePath(string rootPath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return null;
            }

            return Path.IsPathRooted(candidatePath)
                ? Path.GetFullPath(candidatePath)
                : Path.GetFullPath(Path.Combine(rootPath, candidatePath));
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
                return Path.GetFullPath(rootPath);
            }
            catch
            {
                return null;
            }
        }
    }
}
