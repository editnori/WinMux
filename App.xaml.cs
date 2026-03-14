// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace SelfContainedDeployment
{
    public partial class App : Application
    {
        private Window mainWindow;
        private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "native-terminal-starter-crash.log");

        public App()
        {
            this.InitializeComponent();
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            mainWindow = new MainWindow();
            mainWindow.Activate();
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            TryLog("App.UnhandledException", e.Exception?.ToString() ?? e.Message);
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            TryLog("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown exception");
        }

        private static void TryLog(string source, string message)
        {
            try
            {
                File.AppendAllText(
                    CrashLogPath,
                    "==== " + DateTimeOffset.Now.ToString("u") + " ====" + Environment.NewLine +
                    source + Environment.NewLine +
                    message + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
