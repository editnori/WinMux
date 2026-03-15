// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using SelfContainedDeployment.Automation;
using System;

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
            MainWindowInstance?.PersistSessionState();
            automationServer?.Dispose();
        }
    }
}
