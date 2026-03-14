using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Terminal;
using System;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        private int _tabSequence = 1;

        public static MainPage Current;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            WorkspacePathText.Text = Environment.CurrentDirectory;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            EnsureAtLeastOneTab();
            UpdatePaneLayout();
            ShowTerminalShell();
        }

        private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
        {
            ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
            UpdatePaneLayout();
        }

        private void OnTerminalNavClicked(object sender, RoutedEventArgs e)
        {
            ShowTerminalShell();
        }

        private void OnSettingsNavClicked(object sender, RoutedEventArgs e)
        {
            SettingsFrame.Navigate(typeof(SettingsPage));
            SettingsFrame.Visibility = Visibility.Visible;
            TerminalTabs.Visibility = Visibility.Collapsed;
            UpdateRailVisualState(showTerminal: false);
        }

        private void OnNewTabClicked(object sender, RoutedEventArgs e)
        {
            ShowTerminalShell();
            AddTerminalTab();
        }

        private void TerminalTabs_AddTabButtonClick(TabView sender, object args)
        {
            AddTerminalTab();
        }

        private void TerminalTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is TabViewItem item)
            {
                if (item.Content is TerminalControl terminal)
                {
                    terminal.DisposeTerminal();
                }

                sender.TabItems.Remove(item);
            }

            EnsureAtLeastOneTab();
            FocusSelectedTerminal();
        }

        private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FocusSelectedTerminal();
        }

        private void EnsureAtLeastOneTab()
        {
            if (TerminalTabs.TabItems.Count == 0)
            {
                AddTerminalTab();
            }
        }

        private void AddTerminalTab()
        {
            var terminal = new TerminalControl
            {
                InitialWorkingDirectory = Environment.CurrentDirectory,
            };

            var item = new TabViewItem
            {
                Header = $"Session {_tabSequence++}",
                Content = terminal,
                IsClosable = true,
            };

            terminal.SessionTitleChanged += (_, title) => item.Header = FormatTabHeader(title);
            terminal.SessionExited += (_, _) =>
            {
                if (item.Header is string title && !title.EndsWith(" (ended)", StringComparison.Ordinal))
                {
                    item.Header = $"{title} (ended)";
                }
            };

            TerminalTabs.TabItems.Add(item);
            TerminalTabs.SelectedItem = item;
            ShowTerminalShell();
        }

        private void ShowTerminalShell()
        {
            SettingsFrame.Visibility = Visibility.Collapsed;
            TerminalTabs.Visibility = Visibility.Visible;
            UpdateRailVisualState(showTerminal: true);
            FocusSelectedTerminal();
        }

        private void FocusSelectedTerminal()
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Content is TerminalControl terminal)
            {
                terminal.FocusTerminal();
            }
        }

        private void UpdateRailVisualState(bool showTerminal)
        {
            ApplyRailState(TerminalNavButton, TerminalNavText, showTerminal);
            ApplyRailState(SettingsNavButton, SettingsNavText, !showTerminal);
            NewTabRailButton.Foreground = AppBrush("ShellTextSecondaryBrush");
            NewTabText.Foreground = AppBrush("ShellTextSecondaryBrush");
        }

        private void UpdatePaneLayout()
        {
            bool isOpen = ShellSplitView.IsPaneOpen;

            PaneBrandText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            TerminalNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewTabText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

            ToolTipService.SetToolTip(PaneToggleButton, isOpen ? "Collapse sidebar" : "Expand sidebar");
        }

        private static Brush AppBrush(string key)
        {
            return (Brush)Application.Current.Resources[key];
        }

        private static void ApplyRailState(Button button, TextBlock label, bool active)
        {
            button.Background = active ? AppBrush("ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush("ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
            label.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
        }

        private static string FormatTabHeader(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "shell";
            }

            string trimmed = title.Trim().TrimEnd('\\', '/');
            int slashIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));

            if (slashIndex >= 0 && slashIndex < trimmed.Length - 1)
            {
                return trimmed[(slashIndex + 1)..];
            }

            return trimmed.Length > 28 ? trimmed[..28] : trimmed;
        }
    }
}
