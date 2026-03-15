using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Linq;

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
    }
}
