using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace SelfContainedDeployment.Controls
{
    public sealed partial class InspectorFilesPane : UserControl
    {
        public InspectorFilesPane()
        {
            InitializeComponent();
        }

        public TreeView DirectoryTree => DirectoryTreeView;

        public TextBlock DirectoryRootText => DirectoryRootTextBlock;

        public TextBlock DirectoryMetaText => DirectoryMetaTextBlock;

        public TextBlock DirectoryEmptyText => DirectoryEmptyTextBlock;

        public event EventHandler DirectoryItemInvoked;

        private void OnDirectoryTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            DirectoryItemInvoked?.Invoke(this, EventArgs.Empty);
        }

        private void OnDirectoryTreeKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            DirectoryItemInvoked?.Invoke(this, EventArgs.Empty);
        }
    }
}
