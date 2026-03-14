using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace SelfContainedDeployment
{
    public sealed class TerminalSurfaceControl : UserControl
    {
        private readonly TextBlock terminalStatusText;
        private readonly TextBox terminalOutputBox;
        private readonly TextBox terminalInputBox;
        private SidebarSession session;

        public TerminalSurfaceControl()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var commandBar = new Grid();
            commandBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            commandBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commandBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            terminalStatusText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Text = "No terminal session attached."
            };

            var launchButton = new Button { Content = "Launch", Margin = new Thickness(10, 0, 0, 0) };
            launchButton.Click += LaunchButton_Click;

            var stopButton = new Button { Content = "Stop", Margin = new Thickness(10, 0, 0, 0) };
            stopButton.Click += StopButton_Click;

            commandBar.Children.Add(terminalStatusText);
            Grid.SetColumn(launchButton, 1);
            Grid.SetColumn(stopButton, 2);
            commandBar.Children.Add(launchButton);
            commandBar.Children.Add(stopButton);

            terminalOutputBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 14,
                Margin = new Thickness(0, 12, 0, 12)
            };

            var inputBar = new Grid();
            inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            terminalInputBox = new TextBox
            {
                Header = "Send text to terminal"
            };
            terminalInputBox.KeyDown += TerminalInputBox_KeyDown;

            var sendButton = new Button
            {
                Content = "Send",
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            sendButton.Click += SendButton_Click;

            inputBar.Children.Add(terminalInputBox);
            Grid.SetColumn(sendButton, 1);
            inputBar.Children.Add(sendButton);

            root.Children.Add(commandBar);
            Grid.SetRow(terminalOutputBox, 1);
            root.Children.Add(terminalOutputBox);
            Grid.SetRow(inputBar, 2);
            root.Children.Add(inputBar);

            Content = root;
        }

        public void AttachSession(SidebarSession nextSession)
        {
            if (session is not null)
            {
                session.Host.Changed -= Host_Changed;
            }

            session = nextSession;

            if (session is not null)
            {
                session.Host.Changed += Host_Changed;
            }

            Refresh();
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (session is null)
            {
                return;
            }

            await session.Host.EnsureStartedAsync();
            await ResizeToViewportAsync();
            Refresh();
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (session is null)
            {
                return;
            }

            await session.Host.StopAsync();
            Refresh();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentInputAsync();
        }

        private async void TerminalInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            await SendCurrentInputAsync();
        }

        private async Task SendCurrentInputAsync()
        {
            if (session is null)
            {
                return;
            }

            var text = terminalInputBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            terminalInputBox.Text = string.Empty;
            await session.Host.SendLineAsync(text);
            await ResizeToViewportAsync();
            Refresh();
        }

        private void Host_Changed(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }

        private async Task ResizeToViewportAsync()
        {
            if (session is null || !session.Host.IsRunning)
            {
                return;
            }

            var columns = (short)Math.Max(80, (int)(terminalOutputBox.ActualWidth / 8));
            var rows = (short)Math.Max(24, (int)(terminalOutputBox.ActualHeight / 18));
            await session.Host.ResizeAsync(columns, rows);
        }

        private void Refresh()
        {
            if (session is null)
            {
                terminalStatusText.Text = "No terminal session attached.";
                terminalOutputBox.Text = string.Empty;
                return;
            }

            terminalStatusText.Text = session.KindLabel + " • " + session.Host.StatusText;
            terminalOutputBox.Text = session.Host.Buffer.Snapshot().Text;
            terminalOutputBox.SelectionStart = terminalOutputBox.Text.Length;
        }
    }
}
