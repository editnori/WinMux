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
    }
}
