// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;

namespace SelfContainedDeployment
{
    internal static class SampleConfig
    {
        public const string FeatureName = "Native Terminal Starter";
        public const string WindowsWorkingDirectory = @"C:\Users\lqassem\native-terminal-starter";
        public const string WslWorkingDirectory = "/mnt/c/Users/lqassem/native-terminal-starter";
        public static ElementTheme CurrentTheme = ElementTheme.Default;
    }
}
