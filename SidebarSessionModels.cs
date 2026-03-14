using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SelfContainedDeployment
{
    public sealed class SidebarSession : ObservableObject
    {
        private string displayName;
        private string subtitle;
        private TerminalTabItem tab;

        public SidebarSession(string displayName, string kindLabel, string subtitle, TerminalSessionOptions terminalOptions)
        {
            this.displayName = displayName;
            this.subtitle = subtitle;
            KindLabel = kindLabel;
            TerminalOptions = terminalOptions;
            Host = new TerminalSessionHost(terminalOptions);
            Host.Changed += Host_Changed;
        }

        public string DisplayName
        {
            get => displayName;
            set => SetProperty(ref displayName, value);
        }

        public string KindLabel { get; }

        public string Subtitle
        {
            get => subtitle;
            set => SetProperty(ref subtitle, value);
        }

        public TerminalSessionOptions TerminalOptions { get; }

        public TerminalSessionHost Host { get; }

        public TerminalTabItem Tab
        {
            get => tab;
            set => SetProperty(ref tab, value);
        }

        public string StatusText => Host.StatusText;

        public string LaunchSummary => TerminalOptions.BuildCommandLine();

        private void Host_Changed(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    internal static class SidebarSessionSeedBuilder
    {
        public static ObservableCollection<SidebarSession> Create()
        {
            return new ObservableCollection<SidebarSession>
            {
                CreateWslSession("WSL 1"),
                CreatePowerShellSession("PowerShell 1"),
                CreateCodexSession("Codex Thread 1"),
                CreateClaudeSession("Claude Thread 1")
            };
        }

        public static SidebarSession CreateWslSession(string displayName)
        {
            return new SidebarSession(
                displayName,
                "WSL shell",
                "Default distro session wired to the real pseudoterminal backend.",
                new TerminalSessionOptions
                {
                    FileName = "wsl.exe",
                    Arguments = "--cd \"" + SampleConfig.WslWorkingDirectory + "\"",
                    WorkingDirectory = SampleConfig.WindowsWorkingDirectory
                });
        }

        public static SidebarSession CreatePowerShellSession(string displayName)
        {
            return new SidebarSession(
                displayName,
                "Windows PowerShell",
                "Native Windows shell session backed by ConPTY.",
                new TerminalSessionOptions
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo",
                    WorkingDirectory = SampleConfig.WindowsWorkingDirectory
                });
        }

        public static SidebarSession CreateCodexSession(string displayName)
        {
            return new SidebarSession(
                displayName,
                "Codex shell",
                "Codex session anchored to the project folder and launched through the terminal backend.",
                new TerminalSessionOptions
                {
                    FileName = "cmd.exe",
                    Arguments = "/Q /K \"cd /d " + SampleConfig.WindowsWorkingDirectory + " && codex\"",
                    WorkingDirectory = SampleConfig.WindowsWorkingDirectory
                });
        }

        public static SidebarSession CreateClaudeSession(string displayName)
        {
            return new SidebarSession(
                displayName,
                "Claude Code shell",
                "Claude Code session anchored to the project folder and launched through the terminal backend.",
                new TerminalSessionOptions
                {
                    FileName = "cmd.exe",
                    Arguments = "/Q /K \"cd /d " + SampleConfig.WindowsWorkingDirectory + " && claude-code\"",
                    WorkingDirectory = SampleConfig.WindowsWorkingDirectory
                });
        }
    }

    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
