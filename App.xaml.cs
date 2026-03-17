// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using SelfContainedDeployment.Automation;
using System;
using System.IO;

namespace SelfContainedDeployment
{
    public partial class App : Application
    {
        private Window mainWindow;
        private NativeAutomationServer automationServer;

        internal MainWindow MainWindowInstance => mainWindow as MainWindow;

        public App()
        {
            this.InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            mainWindow = new MainWindow();
            mainWindow.Closed += OnMainWindowClosed;
            mainWindow.Activate();
            StartAutomationServerIfRequested();
        }

        private void StartAutomationServerIfRequested()
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("NATIVE_TERMINAL_AUTOMATION_PORT"), out int port) || port <= 0)
            {
                return;
            }

            if (MainWindowInstance is null)
            {
                return;
            }

            automationServer = new NativeAutomationServer(MainWindowInstance, port);
            automationServer.Start();
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            if (sender is MainWindow closedWindow)
            {
                closedWindow.Closed -= OnMainWindowClosed;
                closedWindow.PersistSessionState();
            }

            automationServer?.Dispose();
            automationServer = null;
            mainWindow = null;
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            string path = null;
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinMux");
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, "startup-error.log");
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
            }
            catch
            {
            }

            NativeAutomationDiagnostics.RecordUnhandledException(e.Message, e.Exception?.ToString(), path);
        }
    }
}
