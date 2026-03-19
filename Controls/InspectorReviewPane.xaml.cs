using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace SelfContainedDeployment.Controls
{
    public sealed partial class InspectorReviewPane : UserControl
    {
        public InspectorReviewPane()
        {
            InitializeComponent();
        }

        public FrameworkElement ReviewSourceSection => DiffReviewSourceSectionRoot;

        public ComboBox ReviewSourceComboBox => DiffReviewSourceComboBoxControl;

        public TextBlock ReviewSourceMetaText => DiffReviewSourceMetaTextBlock;

        public TextBlock BranchText => DiffBranchTextBlock;

        public TextBlock WorktreeText => DiffWorktreeTextBlock;

        public TextBlock SummaryText => DiffSummaryTextBlock;

        public ListView DiffFileListView => DiffFileListViewControl;

        public TextBlock DiffEmptyText => DiffEmptyTextBlock;

        public event SelectionChangedEventHandler ReviewSourceSelectionChanged;

        public event RoutedEventHandler DiffFileButtonClicked;

        public event PointerEventHandler DiffFileItemButtonPointerEntered;

        public event PointerEventHandler DiffFileItemButtonPointerExited;

        public event RoutedEventHandler DiffFileItemButtonLoaded;

        public event RoutedEventHandler DiffFileItemButtonUnloaded;

        private void OnReviewSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ReviewSourceSelectionChanged?.Invoke(sender, e);
        }

        private void OnDiffFileButtonClicked(object sender, RoutedEventArgs e)
        {
            DiffFileButtonClicked?.Invoke(sender, e);
        }

        private void OnDiffFileItemButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            DiffFileItemButtonPointerEntered?.Invoke(sender, e);
        }

        private void OnDiffFileItemButtonPointerExited(object sender, PointerRoutedEventArgs e)
        {
            DiffFileItemButtonPointerExited?.Invoke(sender, e);
        }

        private void OnDiffFileItemButtonLoaded(object sender, RoutedEventArgs e)
        {
            DiffFileItemButtonLoaded?.Invoke(sender, e);
        }

        private void OnDiffFileItemButtonUnloaded(object sender, RoutedEventArgs e)
        {
            DiffFileItemButtonUnloaded?.Invoke(sender, e);
        }
    }
}
