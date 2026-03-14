using Microsoft.UI.Xaml;
using SelfContainedDeployment.Shell;

namespace SelfContainedDeployment
{
    internal static class SampleConfig
    {
        public const string FeatureName = "WinMux";
        public static ElementTheme CurrentTheme = ElementTheme.Default;
        public static string DefaultShellProfileId = ShellProfileIds.Wsl;
    }
}
