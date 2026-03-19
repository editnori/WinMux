using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace SelfContainedDeployment.Controls
{
    public sealed partial class InspectorNotesPane : UserControl
    {
        public InspectorNotesPane()
        {
            InitializeComponent();
        }

        public Button ThreadScopeButtonView => ThreadScopeButton;

        public Button ProjectScopeButtonView => ProjectScopeButton;

        public Button InlineAddNoteButtonView => InlineAddNoteButton;

        public Button InlineSaveNoteButtonView => InlineSaveNoteButton;

        public Button InlineDeleteNoteButtonView => InlineDeleteNoteButton;

        public TextBlock NotesMetaText => NotesMetaTextBlock;

        public TextBlock NotesEmptyText => NotesEmptyTextBlock;

        public ItemsControl NotesGroupsItemsControl => NotesGroupsItemsControlView;

        public event RoutedEventHandler ThreadScopeClicked;
        public event RoutedEventHandler ProjectScopeClicked;
        public event RoutedEventHandler AddNoteClicked;
        public event RoutedEventHandler SaveNoteClicked;
        public event RoutedEventHandler DeleteNoteClicked;
        public event RoutedEventHandler ArchivedNotesToggleClicked;
        public event TappedEventHandler NoteCardTapped;
        public event DoubleTappedEventHandler NoteCardDoubleTapped;
        public event PointerEventHandler NoteCardPointerEntered;
        public event PointerEventHandler NoteCardPointerExited;
        public event TextChangedEventHandler NoteCardTitleChanged;
        public event TextChangedEventHandler NoteCardTextChanged;
        public event RoutedEventHandler NoteCardTextBoxGotFocus;
        public event RoutedEventHandler NoteCardTextBoxLostFocus;
        public event KeyEventHandler NoteCardTextBoxKeyDown;
        public event RoutedEventHandler NoteScopeButtonClicked;
        public event RoutedEventHandler SaveNoteCardClicked;
        public event RoutedEventHandler ArchiveNoteButtonClicked;
        public event RoutedEventHandler DeleteNoteCardClicked;

        private void OnThreadScopeButtonClick(object sender, RoutedEventArgs e) => ThreadScopeClicked?.Invoke(sender, e);
        private void OnProjectScopeButtonClick(object sender, RoutedEventArgs e) => ProjectScopeClicked?.Invoke(sender, e);
        private void OnAddNoteButtonClick(object sender, RoutedEventArgs e) => AddNoteClicked?.Invoke(sender, e);
        private void OnSaveNoteButtonClick(object sender, RoutedEventArgs e) => SaveNoteClicked?.Invoke(sender, e);
        private void OnDeleteNoteButtonClick(object sender, RoutedEventArgs e) => DeleteNoteClicked?.Invoke(sender, e);
        private void OnArchivedNotesToggleButtonClick(object sender, RoutedEventArgs e) => ArchivedNotesToggleClicked?.Invoke(sender, e);
        private void OnNoteCardTapped(object sender, TappedRoutedEventArgs e) => NoteCardTapped?.Invoke(sender, e);
        private void OnNoteCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => NoteCardDoubleTapped?.Invoke(sender, e);
        private void OnNoteCardPointerEntered(object sender, PointerRoutedEventArgs e) => NoteCardPointerEntered?.Invoke(sender, e);
        private void OnNoteCardPointerExited(object sender, PointerRoutedEventArgs e) => NoteCardPointerExited?.Invoke(sender, e);
        private void OnNoteCardTitleChanged(object sender, TextChangedEventArgs e) => NoteCardTitleChanged?.Invoke(sender, e);
        private void OnNoteCardTextChanged(object sender, TextChangedEventArgs e) => NoteCardTextChanged?.Invoke(sender, e);
        private void OnNoteCardTextBoxGotFocus(object sender, RoutedEventArgs e) => NoteCardTextBoxGotFocus?.Invoke(sender, e);
        private void OnNoteCardTextBoxLostFocus(object sender, RoutedEventArgs e) => NoteCardTextBoxLostFocus?.Invoke(sender, e);
        private void OnNoteCardTextBoxKeyDown(object sender, KeyRoutedEventArgs e) => NoteCardTextBoxKeyDown?.Invoke(sender, e);
        private void OnNoteScopeButtonClick(object sender, RoutedEventArgs e) => NoteScopeButtonClicked?.Invoke(sender, e);
        private void OnSaveNoteCardButtonClick(object sender, RoutedEventArgs e) => SaveNoteCardClicked?.Invoke(sender, e);
        private void OnArchiveNoteButtonClick(object sender, RoutedEventArgs e) => ArchiveNoteButtonClicked?.Invoke(sender, e);
        private void OnDeleteNoteCardButtonClick(object sender, RoutedEventArgs e) => DeleteNoteCardClicked?.Invoke(sender, e);
    }
}
