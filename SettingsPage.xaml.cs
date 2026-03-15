using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;
using SelfContainedDeployment.Browser;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SelfContainedDeployment
{
    public partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            themePanel.Children.Cast<RadioButton>()
                .First(button => (ElementTheme)button.Tag == SampleConfig.CurrentTheme)
                .IsChecked = true;

            shellProfilePanel.Children.Cast<RadioButton>()
                .First(button => string.Equals((string)button.Tag, SampleConfig.DefaultShellProfileId, System.StringComparison.OrdinalIgnoreCase))
                .IsChecked = true;

            paneLimitBox.Items.Cast<ComboBoxItem>()
                .First(item => string.Equals((string)item.Tag, SampleConfig.MaxPaneCountPerThread.ToString(), System.StringComparison.Ordinal))
                .IsSelected = true;

            RefreshBrowserCredentialVault();

            base.OnNavigatedTo(e);
        }

        private void OnThemeRadioButtonChecked(object sender, RoutedEventArgs e)
        {
            ElementTheme selectedTheme = (ElementTheme)((RadioButton)sender).Tag;
            MainPage.Current?.ApplyTheme(selectedTheme);
        }

        private void OnShellProfileRadioButtonChecked(object sender, RoutedEventArgs e)
        {
            string shellProfileId = (string)((RadioButton)sender).Tag;
            MainPage.Current?.ApplyShellProfile(shellProfileId);
        }

        private void OnPaneLimitSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (paneLimitBox.SelectedItem is not ComboBoxItem item || !int.TryParse((string)item.Tag, out int paneLimit))
            {
                return;
            }

            MainPage.Current?.ApplyPaneLimit(paneLimit);
        }

        private void RefreshBrowserCredentialVault(string statusOverride = null)
        {
            IReadOnlyList<BrowserCredentialSummary> summaries = MainPage.Current?.GetBrowserCredentialSummaries()
                ?? BrowserCredentialStore.GetCredentialSummaries();

            browserCredentialStatusText.Text = statusOverride
                ?? (summaries.Count == 0
                    ? "No imported browser credentials yet."
                    : $"{summaries.Count} encrypted browser credential{(summaries.Count == 1 ? string.Empty : "s")} stored in the WinMux vault.");

            browserCredentialListPanel.Children.Clear();
            if (summaries.Count == 0)
            {
                browserCredentialListPanel.Children.Add(new TextBlock
                {
                    Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                    Text = "Imported sites will appear here. Delete removes them from the WinMux vault only.",
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (BrowserCredentialSummary summary in summaries)
            {
                Grid row = new()
                {
                    ColumnSpacing = 10,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    }
                };

                StackPanel textBlock = new()
                {
                    Spacing = 2,
                };
                textBlock.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(summary.Name) ? summary.Host : $"{summary.Name}  ({summary.Host})",
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                textBlock.Children.Add(new TextBlock
                {
                    Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                    Text = string.IsNullOrWhiteSpace(summary.Username) ? summary.Url : $"{summary.Username}  •  {summary.Url}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(textBlock);

                Button deleteButton = new()
                {
                    Content = "Delete",
                    Style = (Style)Application.Current.Resources["ShellChromeButtonStyle"],
                    Tag = summary.Id,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                AutomationProperties.SetAutomationId(deleteButton, $"settings-browser-delete-{summary.Id}");
                deleteButton.Click += OnDeleteBrowserCredentialClicked;
                Grid.SetColumn(deleteButton, 1);
                row.Children.Add(deleteButton);

                Border container = new()
                {
                    BorderBrush = (Brush)Application.Current.Resources["ShellBorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = row,
                };
                AutomationProperties.SetAutomationId(container, $"settings-browser-entry-{summary.Id}");

                browserCredentialListPanel.Children.Add(container);
            }
        }

        private async void OnImportBrowserCredentialsClicked(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = new();
            picker.FileTypeFilter.Add(".csv");
            picker.SuggestedStartLocation = PickerLocationId.Downloads;

            IntPtr hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            try
            {
                string message = MainPage.Current?.ImportBrowserPasswordsCsvFromPath(file.Path)
                    ?? "Imported browser credentials.";
                RefreshBrowserCredentialVault(message);
            }
            catch (System.Exception ex)
            {
                RefreshBrowserCredentialVault(ex.Message);
            }
        }

        private async void OnAutofillCurrentBrowserClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string message = await MainPage.Current.ManualAutofillSelectedBrowserAsync();
                RefreshBrowserCredentialVault(message);
            }
            catch (System.Exception ex)
            {
                RefreshBrowserCredentialVault(ex.Message);
            }
        }

        private void OnClearBrowserCredentialsClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string message = MainPage.Current?.ClearBrowserCredentialsFromSettings()
                    ?? "Cleared the WinMux browser credential vault.";
                RefreshBrowserCredentialVault(message);
            }
            catch (System.Exception ex)
            {
                RefreshBrowserCredentialVault(ex.Message);
            }
        }

        private void OnDeleteBrowserCredentialClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string id)
            {
                return;
            }

            string message = string.Empty;
            bool ok = MainPage.Current?.DeleteBrowserCredentialFromSettings(id, out message) ?? false;
            RefreshBrowserCredentialVault(message ?? (ok ? "Removed credential from the WinMux vault." : "Could not remove credential."));
        }
    }
}
