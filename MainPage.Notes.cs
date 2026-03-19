using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.UI.Core;

namespace SelfContainedDeployment
{
    public partial class MainPage
    {
        private FrameworkElement InspectorNotesContent => InspectorNotesView;

        private Button InspectorNotesThreadScopeButton => InspectorNotesView?.ThreadScopeButtonView;

        private Button InspectorNotesProjectScopeButton => InspectorNotesView?.ProjectScopeButtonView;

        private Button InspectorInlineAddNoteButton => InspectorNotesView?.InlineAddNoteButtonView;

        private Button InspectorInlineSaveNoteButton => InspectorNotesView?.InlineSaveNoteButtonView;

        private Button InspectorInlineDeleteNoteButton => InspectorNotesView?.InlineDeleteNoteButtonView;

        private TextBlock InspectorNotesMetaText => InspectorNotesView?.NotesMetaText;

        private TextBlock InspectorNotesEmptyText => InspectorNotesView?.NotesEmptyText;

        private ItemsControl InspectorNotesGroupsItemsControl => InspectorNotesView?.NotesGroupsItemsControl;

        private void OnInspectorReviewTabClicked(object sender, RoutedEventArgs e)
        {
            SetInspectorSection(InspectorSection.Review);
        }

        private void OnInspectorFilesTabClicked(object sender, RoutedEventArgs e)
        {
            SetInspectorSection(InspectorSection.Files, refreshFiles: false);
            RefreshInspectorFileBrowser();
        }

        private void OnInspectorNotesTabClicked(object sender, RoutedEventArgs e)
        {
            SetInspectorSection(InspectorSection.Notes, refreshFiles: false);
        }

        private void OnInspectorNotesThreadScopeClicked(object sender, RoutedEventArgs e)
        {
            SetNotesListScope(NotesListScope.Thread);
        }

        private void OnInspectorNotesProjectScopeClicked(object sender, RoutedEventArgs e)
        {
            SetNotesListScope(NotesListScope.Project);
        }

        private void OnInspectorAddNoteClicked(object sender, RoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            StartInspectorNoteDraft(_activeThread, scope: _activeNotesListScope);
        }

        private void OnInspectorDeleteNoteClicked(object sender, RoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            DeleteThreadNote(_activeThread, _activeThread.SelectedNoteId);
        }

        private void OnInspectorSaveNoteClicked(object sender, RoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            WorkspaceThreadNote note = ResolveSelectedThreadNote(_activeThread);
            if (note is null || note.IsArchived)
            {
                return;
            }

            CommitInspectorNoteDraft(_activeThread, note);
        }

        private void OnInspectorNoteCardTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox noteBox || noteBox.Tag is not InspectorNoteCardItem item || item.IsArchived)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            UpdateInspectorNoteDraft(thread, note, text: noteBox.Text, updateText: true);
        }

        private void OnInspectorNoteCardTitleChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox titleBox || titleBox.Tag is not InspectorNoteCardItem item || item.IsArchived)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            UpdateInspectorNoteDraft(thread, note, title: titleBox.Text, updateTitle: true);
        }

        private void OnInspectorSaveNoteCardClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null || note.IsArchived)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            CommitInspectorNoteDraft(thread, note);
        }

        private void OnInspectorNoteCardTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null || string.IsNullOrWhiteSpace(item.NoteId))
            {
                return;
            }

            bool selectionChanged = !string.Equals(thread.SelectedNoteId, item.NoteId, StringComparison.Ordinal);
            thread.SelectedNoteId = item.NoteId;

            if (selectionChanged)
            {
                RefreshInspectorNotes();
                QueueSessionSave();
            }
        }

        private void OnInspectorNoteCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null || string.IsNullOrWhiteSpace(item.NoteId))
            {
                return;
            }

            bool selectionChanged = !string.Equals(thread.SelectedNoteId, item.NoteId, StringComparison.Ordinal);
            thread.SelectedNoteId = item.NoteId;
            if (selectionChanged)
            {
                RefreshInspectorNotes();
                QueueSessionSave();
            }

            if (!item.IsArchived)
            {
                FocusInspectorNoteEditor(item.NoteId);
            }

            e.Handled = true;
        }

        private void OnInspectorNoteCardTextBoxGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox noteBox || noteBox.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            ApplyInspectorNoteEditorFocusState(noteBox, item, focused: true);
            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null || string.IsNullOrWhiteSpace(item.NoteId))
            {
                return;
            }

            thread.SelectedNoteId = item.NoteId;
            QueueSessionSave();
        }

        private void OnInspectorNoteCardTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox noteBox && noteBox.Tag is InspectorNoteCardItem item)
            {
                ApplyInspectorNoteEditorFocusState(noteBox, item, focused: false);
                WorkspaceThread thread = FindThread(item.ThreadId);
                if (thread is not null && !string.IsNullOrWhiteSpace(item.NoteId))
                {
                    thread.SelectedNoteId = item.NoteId;
                }

                UpdateInspectorFileActionState();
                QueueSessionSave();
            }
        }

        private void OnInspectorNoteCardTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox noteBox ||
                noteBox.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (e.Key == Windows.System.VirtualKey.Enter &&
                noteBox.AcceptsReturn &&
                (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down &&
                thread is not null &&
                note is not null)
            {
                thread.SelectedNoteId = item.NoteId;
                CommitInspectorNoteDraft(thread, note);
                e.Handled = true;
                return;
            }

            if (e.Key != Windows.System.VirtualKey.Escape)
            {
                return;
            }

            if (thread is not null && !string.IsNullOrWhiteSpace(item.NoteId))
            {
                thread.SelectedNoteId = item.NoteId;
            }

            DiscardInspectorNoteDraft(item.NoteId, refreshInspector: true);
            InspectorNotesThreadScopeButton?.Focus(FocusState.Programmatic);
            e.Handled = true;
        }

        private void OnInspectorNoteCardPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card && card.Tag is InspectorNoteCardItem item)
            {
                ApplyInspectorNoteCardChrome(card, item, hovered: true);
            }
        }

        private void OnInspectorNoteCardPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card && card.Tag is InspectorNoteCardItem item)
            {
                ApplyInspectorNoteCardChrome(card, item, hovered: false);
            }
        }

        private void OnInspectorNoteScopeButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null)
            {
                return;
            }

            thread.SelectedNoteId = item.NoteId;
            WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (note is not null)
            {
                CommitInspectorNoteDraft(thread, note, refreshInspector: false);
            }

            MenuFlyout flyout = BuildInspectorNoteScopeFlyout(thread, item);
            flyout.ShowAt(button);
        }

        private void OnInspectorNoteScopeOptionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not NoteScopeOption option)
            {
                return;
            }

            WorkspaceThread thread = FindThread(option.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, option.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            CommitInspectorNoteDraft(thread, note, refreshInspector: false);
            UpdateThreadNotePaneAttachment(thread, note, option.PaneId);
        }

        private void OnInspectorArchiveNoteButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            CommitInspectorNoteDraft(thread, note, refreshInspector: false);
            SetThreadNoteArchived(thread, note, archived: !note.IsArchived);
        }

        private void OnInspectorDeleteNoteCardClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null)
            {
                return;
            }

            DeleteThreadNote(thread, item.NoteId);
        }

        private void OnInspectorArchivedNotesToggleClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteGroupItem group || string.IsNullOrWhiteSpace(group.ThreadId))
            {
                return;
            }

            if (!_expandedArchivedNoteThreadIds.Add(group.ThreadId))
            {
                _expandedArchivedNoteThreadIds.Remove(group.ThreadId);
            }

            RefreshInspectorNotes();
        }

        private void SetInspectorSection(InspectorSection section, bool refreshFiles = true)
        {
            bool sectionChanged = _activeInspectorSection != section;
            _activeInspectorSection = section;
            UpdateInspectorSectionChrome();
            if (section == InspectorSection.Files && refreshFiles && sectionChanged)
            {
                RefreshInspectorFileBrowser();
            }
            else if (section == InspectorSection.Review && sectionChanged)
            {
                ApplyGitSnapshotToUi();
            }
            else if (section == InspectorSection.Notes)
            {
                RefreshInspectorNotes(force: sectionChanged);
            }
        }

        private void SyncInspectorSectionWithSelectedPane()
        {
            if (_activeInspectorSection == InspectorSection.Notes)
            {
                return;
            }

            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread);
            if (selectedPane?.Kind == WorkspacePaneKind.Editor)
            {
                SetInspectorSection(InspectorSection.Files, refreshFiles: false);
                return;
            }

            if (selectedPane?.Kind == WorkspacePaneKind.Diff)
            {
                SetInspectorSection(InspectorSection.Review, refreshFiles: false);
            }
        }

        private void UpdateInspectorSectionChrome()
        {
            if (InspectorReviewTabButton is null || InspectorFilesTabButton is null || InspectorNotesTabButton is null)
            {
                return;
            }

            ApplyInspectorTabButtonState(InspectorReviewTabButton, _activeInspectorSection == InspectorSection.Review);
            ApplyInspectorTabButtonState(InspectorFilesTabButton, _activeInspectorSection == InspectorSection.Files);
            ApplyInspectorTabButtonState(InspectorNotesTabButton, _activeInspectorSection == InspectorSection.Notes);
            ApplyInspectorNotesTabButtonAffordance();

            if (InspectorReviewContent is not null)
            {
                InspectorReviewContent.Visibility = _activeInspectorSection == InspectorSection.Review ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorFilesContent is not null)
            {
                InspectorFilesContent.Visibility = _activeInspectorSection == InspectorSection.Files ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorNotesContent is not null)
            {
                InspectorNotesContent.Visibility = _activeInspectorSection == InspectorSection.Notes ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorReviewActionsPanel is not null)
            {
                InspectorReviewActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Review ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorFileActionsPanel is not null)
            {
                InspectorFileActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Files ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorNotesActionsPanel is not null)
            {
                InspectorNotesActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Notes ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ShouldRefreshInspectorNotesUi())
            {
                RefreshInspectorNotes();
            }
            UpdateInspectorFileActionState();
        }

        private void ApplyInspectorNotesTabButtonAffordance()
        {
            if (InspectorNotesTabButton is null)
            {
                return;
            }

            int activeNoteCount = _activeThread?.NoteEntries.Count(candidate => !candidate.IsArchived) ?? 0;
            int archivedNoteCount = _activeThread?.NoteEntries.Count(candidate => candidate.IsArchived) ?? 0;

            ToolTipService.SetToolTip(
                InspectorNotesTabButton,
                _activeThread is null
                    ? "No thread selected"
                    : activeNoteCount == 0 && archivedNoteCount == 0
                        ? "Open project notes"
                        : $"{BuildNotesMeta(_activeProject, _activeThread, NotesListScope.Thread)} in this thread");
        }

        private void SetNotesListScope(NotesListScope scope)
        {
            if (_activeNotesListScope == scope)
            {
                return;
            }

            _activeNotesListScope = scope;
            RefreshInspectorNotes();
        }

        private void StartInspectorNoteDraft(WorkspaceThread thread, string paneId = null, NotesListScope? scope = null)
        {
            if (thread is null)
            {
                return;
            }

            OpenThreadNotes(thread, focusEditor: false, scope: scope);
            string resolvedPaneId = ResolveNotePaneId(thread, paneId, preferSelectedPane: true);
            WorkspaceThreadNote note = thread.NoteEntries
                .FirstOrDefault(candidate =>
                    !candidate.IsArchived &&
                    string.IsNullOrWhiteSpace(candidate.Text) &&
                    IsSystemGeneratedNoteTitle(candidate.Title));

            if (note is null)
            {
                note = AddThreadNote(thread, title: null, text: null, selectAfterCreate: true, paneId: resolvedPaneId);
            }
            else
            {
                thread.SelectedNoteId = note.Id;
                if (!string.IsNullOrWhiteSpace(resolvedPaneId) && !string.Equals(note.PaneId, resolvedPaneId, StringComparison.Ordinal))
                {
                    UpdateThreadNotePaneAttachment(thread, note, resolvedPaneId);
                }
                else
                {
                    RefreshInspectorNotes();
                }
            }

            FocusInspectorNoteEditor(note?.Id);
        }

        private void ClearInspectorNoteDraft()
        {
            if (_activeThread is not null && !string.IsNullOrWhiteSpace(_activeThread.SelectedNoteId))
            {
                _noteDraftsById.Remove(_activeThread.SelectedNoteId);
            }

            UpdateInspectorFileActionState();
        }

        private static string NormalizeNoteDraftTitle(string title)
        {
            return string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
        }

        private static string NormalizeNoteDraftText(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private NoteDraftState ResolveNoteDraftState(WorkspaceThreadNote note)
        {
            return note is not null && _noteDraftsById.TryGetValue(note.Id, out NoteDraftState draft)
                ? draft
                : null;
        }

        private NoteDraftState GetOrCreateNoteDraftState(WorkspaceThreadNote note)
        {
            if (note is null)
            {
                return null;
            }

            if (!_noteDraftsById.TryGetValue(note.Id, out NoteDraftState draft))
            {
                draft = new NoteDraftState
                {
                    EditableTitle = ResolveEditableNoteTitle(note),
                    Text = note.Text,
                };
                _noteDraftsById[note.Id] = draft;
            }

            draft.Dirty = IsNoteDraftDirty(note, draft);
            return draft;
        }

        private static bool IsNoteDraftDirty(WorkspaceThreadNote note, NoteDraftState draft)
        {
            if (note is null || draft is null)
            {
                return false;
            }

            return !string.Equals(ResolveEditableNoteTitle(note), draft.EditableTitle ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(NormalizeNoteDraftText(note.Text), NormalizeNoteDraftText(draft.Text), StringComparison.Ordinal);
        }

        private void UpdateInspectorNoteDraft(WorkspaceThread thread, WorkspaceThreadNote note, string title = null, string text = null, bool updateTitle = false, bool updateText = false)
        {
            if (thread is null || note is null || note.IsArchived)
            {
                return;
            }

            NoteDraftState draft = GetOrCreateNoteDraftState(note);
            if (draft is null)
            {
                return;
            }

            if (updateTitle)
            {
                draft.EditableTitle = NormalizeNoteDraftTitle(title);
            }

            if (updateText)
            {
                draft.Text = NormalizeNoteDraftText(text);
            }

            draft.Dirty = IsNoteDraftDirty(note, draft);
            if (!draft.Dirty)
            {
                _noteDraftsById.Remove(note.Id);
            }

            thread.SelectedNoteId = note.Id;
            UpdateInspectorFileActionState();
        }

        private bool CommitInspectorNoteDraft(WorkspaceThread thread, WorkspaceThreadNote note, bool refreshInspector = true)
        {
            if (thread is null || note is null || note.IsArchived)
            {
                return false;
            }

            if (!_noteDraftsById.TryGetValue(note.Id, out NoteDraftState draft))
            {
                UpdateInspectorFileActionState();
                return false;
            }

            string nextTitle = string.IsNullOrWhiteSpace(draft.EditableTitle)
                ? BuildDefaultThreadNoteTitle(thread)
                : draft.EditableTitle.Trim();
            string nextText = NormalizeNoteDraftText(draft.Text);
            bool changed = !string.Equals(note.Title, nextTitle, StringComparison.Ordinal) ||
                !string.Equals(NormalizeNoteDraftText(note.Text), nextText, StringComparison.Ordinal);

            _noteDraftsById.Remove(note.Id);

            if (!changed)
            {
                if (refreshInspector)
                {
                    RefreshInspectorNotes();
                }
                else
                {
                    UpdateInspectorFileActionState();
                }

                return false;
            }

            note.Title = nextTitle;
            note.Text = nextText;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread, refreshInspector);
            return true;
        }

        private void DiscardInspectorNoteDraft(string noteId, bool refreshInspector)
        {
            if (string.IsNullOrWhiteSpace(noteId))
            {
                return;
            }

            if (_noteDraftsById.Remove(noteId) && refreshInspector)
            {
                RefreshInspectorNotes();
                return;
            }

            UpdateInspectorFileActionState();
        }

        private void ApplyNotesListScopeButtonState(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            Brush accentBrush = AppBrush(button, "ShellPaneActiveBorderBrush");
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            button.Background = active
                ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x16 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : null;
            button.BorderBrush = null;
            button.BorderThickness = new Thickness(0);
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private void ApplyInspectorNoteCardChrome(Border card, InspectorNoteCardItem item, bool hovered)
        {
            if (card is null || item is null)
            {
                return;
            }

            if (!hovered)
            {
                card.Background = item.CardBackground;
                card.BorderBrush = item.CardBorderBrush;
                card.BorderThickness = new Thickness(0);
                return;
            }

            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            if (item.IsSelected)
            {
                byte backgroundAlpha = item.IsArchived
                    ? (byte)(lightTheme ? 0x0B : 0x0A)
                    : (byte)(lightTheme ? 0x14 : 0x12);
                card.Background = CreateSidebarTintedBrush(item.AccentBrush, backgroundAlpha, Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B));
            }
            else
            {
                card.Background = AppBrush(card, "ShellNavHoverBrush");
            }

            card.BorderBrush = null;
            card.BorderThickness = new Thickness(0);
        }

        private void ApplyInspectorNoteEditorFocusState(TextBox editor, InspectorNoteCardItem item, bool focused)
        {
            if (editor is null || item is null)
            {
                return;
            }

            if (!focused)
            {
                editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                editor.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                return;
            }

            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            editor.Background = CreateSidebarTintedBrush(item.AccentBrush, lightTheme ? (byte)0x0A : (byte)0x08, Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B));
            editor.BorderBrush = CreateSidebarTintedBrush(item.AccentBrush, lightTheme ? (byte)0x54 : (byte)0x44, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
        }

        private MenuFlyout BuildInspectorNoteScopeFlyout(WorkspaceThread thread, InspectorNoteCardItem item)
        {
            MenuFlyout flyout = new();
            foreach (NoteScopeOption option in BuildNoteScopeOptions(thread, item?.NoteId))
            {
                MenuFlyoutItem menuItem = new()
                {
                    Text = option.Label,
                    Tag = option,
                };
                menuItem.Click += OnInspectorNoteScopeOptionClicked;
                flyout.Items.Add(menuItem);
            }

            return flyout;
        }

        private void FocusInspectorNoteEditor(string noteId)
        {
            if (string.IsNullOrWhiteSpace(noteId))
            {
                return;
            }

            string automationId = BuildNoteEditorAutomationId(noteId);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (FindFirstElement(ShellRoot, candidate =>
                        candidate is TextBox textBox &&
                        string.Equals(AutomationProperties.GetAutomationId(textBox), automationId, StringComparison.Ordinal)) is not TextBox noteEditor)
                {
                    return;
                }

                noteEditor.Focus(FocusState.Programmatic);
                noteEditor.Select(noteEditor.Text?.Length ?? 0, 0);
            });
        }

        private void RefreshInspectorNotes(bool force = false)
        {
            if (InspectorNotesMetaText is null ||
                InspectorNotesGroupsItemsControl is null ||
                InspectorNotesEmptyText is null ||
                InspectorNotesThreadScopeButton is null ||
                InspectorNotesProjectScopeButton is null)
            {
                return;
            }

            string renderKey = BuildInspectorNotesRenderKey(_activeProject, _activeThread, _activeNotesListScope);
            if (force || !string.Equals(renderKey, _lastInspectorNotesRenderKey, StringComparison.Ordinal))
            {
                List<InspectorNoteGroupItem> noteGroups = BuildInspectorNoteGroups(_activeProject, _activeThread, _activeNotesListScope).ToList();
                InspectorNotesGroupsItemsControl.ItemsSource = noteGroups;

                InspectorNotesEmptyText.Text = _activeNotesListScope == NotesListScope.Thread
                    ? "No thread notes yet. Add a note to pin context here."
                    : "No project notes yet.";
                InspectorNotesEmptyText.Visibility = noteGroups.Sum(candidate => candidate.ActiveNotes.Count + candidate.ArchivedNotes.Count) == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _lastInspectorNotesRenderKey = renderKey;
            }

            InspectorNotesThreadScopeButton.IsEnabled = _activeThread is not null;
            InspectorNotesProjectScopeButton.IsEnabled = _activeProject is not null;
            ApplyNotesListScopeButtonState(InspectorNotesThreadScopeButton, _activeNotesListScope == NotesListScope.Thread);
            ApplyNotesListScopeButtonState(InspectorNotesProjectScopeButton, _activeNotesListScope == NotesListScope.Project);
            UpdateInspectorFileActionState();
            InspectorNotesMetaText.Text = BuildNotesMeta(_activeProject, _activeThread, _activeNotesListScope);
            InspectorNotesMetaText.Foreground = AppBrush(InspectorNotesMetaText, "ShellTextSecondaryBrush");
        }

        private void OpenThreadNotes(WorkspaceThread thread, bool focusEditor = true, NotesListScope? scope = null)
        {
            if (thread is null)
            {
                return;
            }

            WorkspaceThreadNote noteToFocus = focusEditor ? ResolveSelectedThreadNote(thread) : null;

            if (!ReferenceEquals(_activeThread, thread) || !ReferenceEquals(_activeProject, FindProjectForThread(thread)))
            {
                ClearInspectorNoteDraft();
                ActivateThread(thread);
            }

            ShowTerminalShellIfNeeded(queueGitRefresh: false);
            if (scope.HasValue)
            {
                _activeNotesListScope = scope.Value;
            }

            if (!_inspectorOpen)
            {
                _inspectorOpen = true;
                UpdateInspectorVisibility();
                QueueSessionSave();
            }

            SetInspectorSection(InspectorSection.Notes, refreshFiles: false);

            if (focusEditor)
            {
                FocusInspectorNoteEditor(noteToFocus?.Id);
            }
        }

        private bool UpdateThreadNotes(WorkspaceThread thread, string nextNotes, bool refreshInspector = true)
        {
            if (thread is null)
            {
                return false;
            }

            WorkspaceThreadNote note = ResolveSelectedThreadNote(thread);
            if (note is null && string.IsNullOrWhiteSpace(nextNotes))
            {
                if (refreshInspector && ReferenceEquals(thread, _activeThread))
                {
                    RefreshInspectorNotes();
                }

                return false;
            }

            if (note is null)
            {
                AddThreadNote(thread, title: null, text: nextNotes, selectAfterCreate: true);
                return true;
            }

            return UpdateThreadNoteText(thread, note, nextNotes, refreshInspector);
        }

        private WorkspaceThreadNote ResolveSelectedThreadNote(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return null;
            }

            if (thread.NoteEntries.Count == 0)
            {
                thread.SelectedNoteId = null;
                return null;
            }

            WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.SelectedNoteId, StringComparison.Ordinal))
                ?? ResolvePreferredThreadNote(thread);
            thread.SelectedNoteId = note.Id;
            return note;
        }

        private static WorkspaceThreadNote ResolvePreferredThreadNote(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return null;
            }

            return thread.NoteEntries
                .Where(candidate => !candidate.IsArchived)
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .FirstOrDefault()
                ?? thread.NoteEntries.OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault();
        }

        private static string BuildDefaultThreadNoteTitle(WorkspaceThread thread)
        {
            int nextIndex = Math.Max(1, (thread?.NoteEntries.Count ?? 0) + 1);
            return nextIndex == 1 ? "Note" : $"Note {nextIndex}";
        }

        private string ResolveNotePaneId(WorkspaceThread thread, string requestedPaneId = null, bool preferSelectedPane = true)
        {
            if (thread is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(requestedPaneId) &&
                thread.Panes.Any(candidate => string.Equals(candidate.Id, requestedPaneId, StringComparison.Ordinal)))
            {
                return requestedPaneId;
            }

            return preferSelectedPane
                ? GetSelectedPane(thread)?.Id
                : null;
        }

        private WorkspaceThreadNote AddThreadNote(WorkspaceThread thread, string title, string text, bool selectAfterCreate, string paneId = null)
        {
            if (thread is null)
            {
                return null;
            }

            WorkspaceThreadNote note = new(
                string.IsNullOrWhiteSpace(title) ? BuildDefaultThreadNoteTitle(thread) : title,
                text)
            {
                PaneId = ResolveNotePaneId(thread, paneId, preferSelectedPane: false),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            thread.NoteEntries.Insert(0, note);
            if (selectAfterCreate)
            {
                thread.SelectedNoteId = note.Id;
            }

            AfterThreadNotesChanged(thread);
            return note;
        }

        private WorkspaceThreadNote UpsertThreadNote(WorkspaceThread thread, string noteId, string title, string text, bool selectAfterUpdate, string paneId = null)
        {
            if (thread is null)
            {
                return null;
            }

            WorkspaceThreadNote note = string.IsNullOrWhiteSpace(noteId)
                ? ResolveSelectedThreadNote(thread)
                : thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, noteId, StringComparison.Ordinal));

            if (note is null)
            {
                return AddThreadNote(thread, title, text, selectAfterCreate: true, paneId);
            }

            bool changed = false;
            string resolvedTitle = string.IsNullOrWhiteSpace(title) ? note.Title : title.Trim();
            if (!string.Equals(note.Title, resolvedTitle, StringComparison.Ordinal))
            {
                note.Title = resolvedTitle;
                changed = true;
            }

            string normalizedText = string.IsNullOrWhiteSpace(text) ? null : text;
            if (!string.Equals(note.Text, normalizedText, StringComparison.Ordinal))
            {
                note.Text = normalizedText;
                changed = true;
            }

            string resolvedPaneId = paneId is null
                ? note.PaneId
                : ResolveNotePaneId(thread, paneId, preferSelectedPane: false);
            if (!string.Equals(note.PaneId, resolvedPaneId, StringComparison.Ordinal))
            {
                note.PaneId = resolvedPaneId;
                changed = true;
            }

            if (!changed)
            {
                if (selectAfterUpdate)
                {
                    thread.SelectedNoteId = note.Id;
                }

                return note;
            }

            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            if (selectAfterUpdate)
            {
                thread.SelectedNoteId = note.Id;
            }

            AfterThreadNotesChanged(thread);
            return note;
        }

        private bool UpdateThreadNoteText(WorkspaceThread thread, WorkspaceThreadNote note, string text, bool refreshInspector)
        {
            if (thread is null || note is null)
            {
                return false;
            }

            string normalizedText = string.IsNullOrWhiteSpace(text) ? null : text;
            if (string.Equals(note.Text, normalizedText, StringComparison.Ordinal))
            {
                if (refreshInspector && ReferenceEquals(thread, _activeThread))
                {
                    RefreshInspectorNotes();
                }

                return false;
            }

            note.Text = normalizedText;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread, refreshInspector);
            return true;
        }

        private bool UpdateThreadNoteTitle(WorkspaceThread thread, WorkspaceThreadNote note, string title, bool refreshInspector)
        {
            if (thread is null || note is null)
            {
                return false;
            }

            string normalizedTitle = string.IsNullOrWhiteSpace(title) ? BuildDefaultThreadNoteTitle(thread) : title.Trim();
            if (string.Equals(note.Title, normalizedTitle, StringComparison.Ordinal))
            {
                if (refreshInspector && ReferenceEquals(thread, _activeThread))
                {
                    RefreshInspectorNotes();
                }

                return false;
            }

            note.Title = normalizedTitle;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread, refreshInspector);
            return true;
        }

        private bool DeleteThreadNote(WorkspaceThread thread, string noteId)
        {
            if (thread is null)
            {
                return false;
            }

            WorkspaceThreadNote note = string.IsNullOrWhiteSpace(noteId)
                ? ResolveSelectedThreadNote(thread)
                : thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, noteId, StringComparison.Ordinal));
            if (note is null)
            {
                return false;
            }

            _noteDraftsById.Remove(note.Id);
            thread.NoteEntries.Remove(note);
            thread.SelectedNoteId = ResolvePreferredThreadNote(thread)?.Id;
            AfterThreadNotesChanged(thread);
            return true;
        }

        private bool SelectThreadNote(WorkspaceThread thread, string noteId, bool navigateToAttachment = true)
        {
            if (thread is null || string.IsNullOrWhiteSpace(noteId))
            {
                return false;
            }

            WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, noteId, StringComparison.Ordinal));
            if (note is null)
            {
                return false;
            }

            if (!ReferenceEquals(thread, _activeThread))
            {
                ActivateThread(thread);
            }

            thread.SelectedNoteId = note.Id;
            if (note.IsArchived)
            {
                _expandedArchivedNoteThreadIds.Add(thread.Id);
            }

            if (navigateToAttachment && !string.IsNullOrWhiteSpace(note.PaneId))
            {
                SelectTab(note.PaneId);
            }

            RefreshInspectorNotes();
            QueueProjectTreeRefresh();
            QueueSessionSave();
            return true;
        }

        private bool UpdateThreadNotePaneAttachment(WorkspaceThread thread, WorkspaceThreadNote note, string paneId)
        {
            if (thread is null || note is null)
            {
                return false;
            }

            string resolvedPaneId = !string.IsNullOrWhiteSpace(paneId) &&
                thread.Panes.Any(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal))
                ? paneId
                : null;
            if (string.Equals(note.PaneId, resolvedPaneId, StringComparison.Ordinal))
            {
                return false;
            }

            note.PaneId = resolvedPaneId;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread);
            return true;
        }

        private bool SetThreadNoteArchived(WorkspaceThread thread, WorkspaceThreadNote note, bool archived)
        {
            if (thread is null || note is null || note.IsArchived == archived)
            {
                return false;
            }

            note.ArchivedAt = archived ? DateTimeOffset.UtcNow : null;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            if (archived)
            {
                thread.NoteEntries.Add(note);
                _expandedArchivedNoteThreadIds.Add(thread.Id);
            }
            else
            {
                thread.NoteEntries.Insert(0, note);
            }

            thread.SelectedNoteId = archived
                ? ResolvePreferredThreadNote(thread)?.Id ?? note.Id
                : note.Id;
            AfterThreadNotesChanged(thread);
            return true;
        }

        private void ClearPaneNoteAttachments(WorkspaceThread thread, string paneId)
        {
            if (thread is null || string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            bool changed = false;
            foreach (WorkspaceThreadNote note in thread.NoteEntries.Where(candidate => string.Equals(candidate.PaneId, paneId, StringComparison.Ordinal)))
            {
                note.PaneId = null;
                note.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (changed)
            {
                AfterThreadNotesChanged(thread);
            }
        }

        private void AfterThreadNotesChanged(WorkspaceThread thread, bool refreshInspector = true)
        {
            if (thread is not null)
            {
                HashSet<string> liveNoteIds = thread.NoteEntries
                    .Select(note => note.Id)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string staleNoteId in _noteDraftsById.Keys.Where(noteId => !liveNoteIds.Contains(noteId)).ToList())
                {
                    _noteDraftsById.Remove(staleNoteId);
                }
            }

            QueueProjectTreeRefresh();
            QueueSessionSave();

            if (refreshInspector && ShouldRefreshInspectorNotesForThread(thread))
            {
                RefreshInspectorNotes();
            }
        }

        private bool ShouldRefreshInspectorNotesForThread(WorkspaceThread thread)
        {
            if (thread is null || _activeInspectorSection != InspectorSection.Notes)
            {
                return false;
            }

            return _activeNotesListScope == NotesListScope.Project
                ? ReferenceEquals(thread.Project, _activeProject)
                : ReferenceEquals(thread, _activeThread);
        }

        private bool ShouldRefreshInspectorNotesUi()
        {
            return _inspectorOpen &&
                !_showingSettings &&
                _activeInspectorSection == InspectorSection.Notes &&
                _activeThread is not null;
        }

        private string BuildInspectorNotesRenderKey(WorkspaceProject project, WorkspaceThread thread, NotesListScope scope)
        {
            StringBuilder builder = new(512);
            AppendInspectorNotesRenderKeyValue(builder, scope.ToString());
            AppendInspectorNotesRenderKeyValue(builder, project?.Id);
            AppendInspectorNotesRenderKeyValue(builder, thread?.Id);
            AppendInspectorNotesRenderKeyValue(builder, _activeThread?.Id);
            AppendInspectorNotesRenderKeyValue(builder, ResolveTheme(SampleConfig.CurrentTheme).ToString());
            AppendInspectorNotesRenderKeyValue(builder, SampleConfig.CurrentThemePackId);

            if (scope == NotesListScope.Thread)
            {
                AppendInspectorNotesThreadRenderKey(builder, thread, scope);
                return builder.ToString();
            }

            if (project is null)
            {
                return builder.ToString();
            }

            foreach (WorkspaceThread ownerThread in project.Threads
                         .Where(candidate => candidate.NoteEntries.Count > 0)
                         .OrderByDescending(candidate => ReferenceEquals(candidate, _activeThread))
                         .ThenByDescending(GetLatestThreadNoteActivity))
            {
                AppendInspectorNotesThreadRenderKey(builder, ownerThread, scope);
            }

            return builder.ToString();
        }

        private void AppendInspectorNotesThreadRenderKey(StringBuilder builder, WorkspaceThread thread, NotesListScope scope)
        {
            AppendInspectorNotesRenderKeyValue(builder, thread?.Id);
            AppendInspectorNotesRenderKeyValue(builder, thread?.Name);
            AppendInspectorNotesRenderKeyValue(builder, thread?.SelectedNoteId);
            AppendInspectorNotesRenderKeyValue(builder, thread?.SelectedPaneId);
            if (thread is null)
            {
                return;
            }

            int activeNoteCount = 0;
            foreach (WorkspaceThreadNote note in thread.NoteEntries)
            {
                if (!note.IsArchived)
                {
                    activeNoteCount++;
                }
            }

            AppendInspectorNotesRenderKeyValue(
                builder,
                (_expandedArchivedNoteThreadIds.Contains(thread.Id) || (scope == NotesListScope.Thread && activeNoteCount == 0)).ToString());

            foreach (WorkspacePaneRecord pane in thread.Panes)
            {
                AppendInspectorNotesRenderKeyValue(builder, pane.Id);
                AppendInspectorNotesRenderKeyValue(builder, pane.Kind.ToString());
                AppendInspectorNotesRenderKeyValue(builder, pane.Title);
            }

            foreach (WorkspaceThreadNote note in thread.NoteEntries)
            {
                NoteDraftState draft = ResolveNoteDraftState(note);
                AppendInspectorNotesRenderKeyValue(builder, note.Id);
                AppendInspectorNotesRenderKeyValue(builder, note.Title);
                AppendInspectorNotesRenderKeyValue(builder, note.Text);
                AppendInspectorNotesRenderKeyValue(builder, note.PaneId);
                AppendInspectorNotesRenderKeyValue(builder, note.IsArchived.ToString());
                AppendInspectorNotesRenderKeyValue(builder, note.CreatedAt.UtcTicks.ToString());
                AppendInspectorNotesRenderKeyValue(builder, note.UpdatedAt.UtcTicks.ToString());
                AppendInspectorNotesRenderKeyValue(builder, note.ArchivedAt?.UtcTicks.ToString());
                AppendInspectorNotesRenderKeyValue(builder, draft?.EditableTitle);
                AppendInspectorNotesRenderKeyValue(builder, draft?.Text);
                AppendInspectorNotesRenderKeyValue(builder, draft?.Dirty.ToString());
            }
        }

        private static void AppendInspectorNotesRenderKeyValue(StringBuilder builder, string value)
        {
            builder.Append(value?.Length ?? -1);
            builder.Append(':');
            builder.Append(value);
            builder.Append('|');
        }

        private IEnumerable<InspectorNoteGroupItem> BuildInspectorNoteGroups(WorkspaceProject project, WorkspaceThread thread, NotesListScope scope)
        {
            if (scope == NotesListScope.Thread)
            {
                InspectorNoteGroupItem group = BuildInspectorNoteGroup(thread, scope);
                if (group is not null)
                {
                    yield return group;
                }

                yield break;
            }

            if (project is null)
            {
                yield break;
            }

            foreach (WorkspaceThread ownerThread in project.Threads
                         .Where(candidate => candidate.NoteEntries.Count > 0)
                         .OrderByDescending(candidate => ReferenceEquals(candidate, _activeThread))
                         .ThenByDescending(GetLatestThreadNoteActivity))
            {
                InspectorNoteGroupItem group = BuildInspectorNoteGroup(ownerThread, scope);
                if (group is not null)
                {
                    yield return group;
                }
            }
        }

        private InspectorNoteGroupItem BuildInspectorNoteGroup(WorkspaceThread thread, NotesListScope scope)
        {
            if (thread is null)
            {
                return null;
            }

            List<WorkspaceThreadNote> activeNotes = thread.NoteEntries
                .Where(candidate => !candidate.IsArchived)
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .ToList();
            List<WorkspaceThreadNote> archivedNotes = thread.NoteEntries
                .Where(candidate => candidate.IsArchived)
                .OrderByDescending(candidate => candidate.ArchivedAt ?? candidate.UpdatedAt)
                .ToList();
            if (activeNotes.Count == 0 && archivedNotes.Count == 0)
            {
                return null;
            }

            bool showHeader = scope == NotesListScope.Project || archivedNotes.Count > 0;
            bool archivedExpanded = _expandedArchivedNoteThreadIds.Contains(thread.Id) || (scope == NotesListScope.Thread && activeNotes.Count == 0);
            return new InspectorNoteGroupItem
            {
                ThreadId = thread.Id,
                Title = thread.Name,
                Meta = showHeader ? BuildNoteGroupMeta(thread, activeNotes.Count, archivedNotes.Count, scope) : string.Empty,
                HeaderAccentBrush = ResolveNoteGroupAccentBrush(thread, activeNotes, archivedNotes),
                HeaderVisibility = showHeader ? Visibility.Visible : Visibility.Collapsed,
                ArchivedToggleText = archivedExpanded
                    ? "Hide archived"
                    : $"Archived ({archivedNotes.Count})",
                ArchivedSectionVisibility = archivedNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                ArchivedItemsVisibility = archivedExpanded ? Visibility.Visible : Visibility.Collapsed,
                ActiveNotes = activeNotes.Select(note => BuildInspectorNoteCardItem(thread, note)).ToList(),
                ArchivedNotes = archivedNotes.Select(note => BuildInspectorNoteCardItem(thread, note)).ToList(),
            };
        }

        private string BuildNoteGroupMeta(WorkspaceThread thread, int activeCount, int archivedCount, NotesListScope scope)
        {
            int totalCount = activeCount + archivedCount;
            List<string> parts = new();
            parts.Add(totalCount == 1 ? "1 note" : $"{totalCount} notes");

            if (archivedCount > 0)
            {
                parts.Add($"{archivedCount} archived");
            }

            if (scope == NotesListScope.Project && ReferenceEquals(thread, _activeThread))
            {
                parts.Add("Current");
            }

            return string.Join(" · ", parts);
        }

        private static string ResolveNoteAccentBrushKey(WorkspacePaneRecord attachedPane)
        {
            return attachedPane?.Kind switch
            {
                WorkspacePaneKind.Browser => "ShellInfoBrush",
                WorkspacePaneKind.Editor => "ShellSuccessBrush",
                WorkspacePaneKind.Diff => "ShellConfigBrush",
                WorkspacePaneKind.Terminal => "ShellTerminalBrush",
                _ => "ShellPaneActiveBorderBrush",
            };
        }

        private Brush ResolveNoteAccentBrush(WorkspacePaneRecord attachedPane)
        {
            return AppBrush(InspectorNotesGroupsItemsControl, ResolveNoteAccentBrushKey(attachedPane));
        }

        private Brush ResolveNoteGroupAccentBrush(
            WorkspaceThread thread,
            IReadOnlyList<WorkspaceThreadNote> activeNotes,
            IReadOnlyList<WorkspaceThreadNote> archivedNotes)
        {
            WorkspaceThreadNote sampleNote = activeNotes?.FirstOrDefault() ?? archivedNotes?.FirstOrDefault();
            WorkspacePaneRecord attachedPane = thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, sampleNote?.PaneId, StringComparison.Ordinal))
                ?? thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.SelectedPaneId, StringComparison.Ordinal))
                ?? thread?.Panes.FirstOrDefault();
            return ResolveNoteAccentBrush(attachedPane);
        }

        private InspectorNoteCardItem BuildInspectorNoteCardItem(WorkspaceThread thread, WorkspaceThreadNote note)
        {
            WorkspacePaneRecord attachedPane = thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, note?.PaneId, StringComparison.Ordinal));
            Brush accentBrush = ResolveNoteAccentBrush(attachedPane);
            bool selected = string.Equals(thread?.SelectedNoteId, note?.Id, StringComparison.Ordinal);
            bool archived = note?.IsArchived == true;
            NoteDraftState draft = ResolveNoteDraftState(note);
            bool dirty = draft?.Dirty == true;
            string noteText = draft?.Text ?? note?.Text;
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            return new InspectorNoteCardItem
            {
                NoteId = note?.Id,
                ThreadId = thread?.Id,
                Title = ResolveNoteListTitle(note),
                EditableTitle = draft?.EditableTitle ?? ResolveEditableNoteTitle(note),
                TitlePlaceholderText = archived ? string.Empty : "Optional title",
                TitleEditorAutomationId = BuildNoteTitleEditorAutomationId(note?.Id),
                Text = noteText,
                Meta = BuildNoteCardMeta(attachedPane),
                StatusText = archived ? "Read only" : dirty ? "Unsaved changes" : string.Empty,
                TimestampText = archived
                    ? $"Archived {FormatNoteTimestamp(note?.ArchivedAt ?? note?.UpdatedAt ?? DateTimeOffset.UtcNow)}"
                    : dirty
                        ? $"Last saved {FormatNoteTimestamp(note?.UpdatedAt ?? DateTimeOffset.UtcNow)}"
                        : $"Saved {FormatNoteTimestamp(note?.UpdatedAt ?? DateTimeOffset.UtcNow)}",
                ScopeButtonLabel = BuildNoteScopeLabel(attachedPane),
                ScopeToolTip = "Attach this note to the thread or a pane",
                ArchiveButtonLabel = archived ? "Restore" : "Archive",
                ArchiveToolTip = archived ? "Restore this note to the active list" : "Archive this note",
                DeleteToolTip = "Delete this note",
                PlaceholderText = archived ? string.Empty : "Write note",
                EditorAutomationId = BuildNoteEditorAutomationId(note?.Id),
                AccentBrush = accentBrush,
                CardBackground = selected
                    ? CreateSidebarTintedBrush(
                        accentBrush,
                        archived ? (byte)(lightTheme ? 0x06 : 0x07) : (byte)(lightTheme ? 0x0E : 0x0C),
                        Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B))
                    : null,
                CardBorderBrush = null,
                ArchiveButtonVisibility = string.IsNullOrWhiteSpace(noteText) && string.IsNullOrWhiteSpace(draft?.EditableTitle) && !archived
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                IsArchived = archived,
                IsSelected = selected,
            };
        }

        private string BuildNoteCardMeta(WorkspacePaneRecord attachedPane)
        {
            return attachedPane is null
                ? _activeNotesListScope == NotesListScope.Project ? "Thread note" : "Current thread"
                : BuildPaneContextTitle(attachedPane);
        }

        private static string BuildNoteScopeLabel(WorkspacePaneRecord attachedPane)
        {
            return attachedPane?.Kind switch
            {
                WorkspacePaneKind.Browser => "WEB",
                WorkspacePaneKind.Editor => "EDIT",
                WorkspacePaneKind.Diff => "DIFF",
                WorkspacePaneKind.Terminal => "TERM",
                _ => "THREAD",
            };
        }

        private static string BuildPaneContextTitle(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return string.Empty;
            }

            string title = string.IsNullOrWhiteSpace(pane.Title) ? string.Empty : pane.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return BuildOverviewPaneLabel(pane);
            }

            string normalized = pane.Kind switch
            {
                WorkspacePaneKind.Browser when title.StartsWith("Web ", StringComparison.OrdinalIgnoreCase) => title[4..],
                WorkspacePaneKind.Editor when title.StartsWith("Edit ", StringComparison.OrdinalIgnoreCase) => title[5..],
                WorkspacePaneKind.Diff when title.StartsWith("Diff ", StringComparison.OrdinalIgnoreCase) => title[5..],
                _ => title,
            };

            normalized = normalized.Trim().TrimEnd('\\', '/');
            int separatorIndex = Math.Max(normalized.LastIndexOf('\\'), normalized.LastIndexOf('/'));
            if (separatorIndex >= 0 && separatorIndex < normalized.Length - 1)
            {
                normalized = normalized[(separatorIndex + 1)..];
            }

            return normalized.Length > 32
                ? normalized[..29] + "..."
                : normalized;
        }

        private static string BuildNotesMeta(WorkspaceProject project, WorkspaceThread thread, NotesListScope scope)
        {
            IEnumerable<WorkspaceThreadNote> notes = scope == NotesListScope.Thread
                ? thread?.NoteEntries ?? Enumerable.Empty<WorkspaceThreadNote>()
                : project?.Threads.SelectMany(candidate => candidate.NoteEntries) ?? Enumerable.Empty<WorkspaceThreadNote>();
            int activeCount = notes.Count(candidate => !candidate.IsArchived);
            int archivedCount = notes.Count(candidate => candidate.IsArchived);
            if (activeCount == 0 && archivedCount == 0)
            {
                return "No notes";
            }

            List<string> parts = new();
            if (activeCount > 0)
            {
                parts.Add($"{activeCount} active");
            }

            if (archivedCount > 0)
            {
                parts.Add($"{archivedCount} archived");
            }

            return string.Join(" · ", parts);
        }

        private static IEnumerable<NoteScopeOption> BuildNoteScopeOptions(WorkspaceThread thread, string noteId)
        {
            yield return new NoteScopeOption
            {
                ThreadId = thread?.Id,
                NoteId = noteId,
                PaneId = null,
                Label = "Thread",
            };

            if (thread is null)
            {
                yield break;
            }

            foreach (WorkspacePaneRecord pane in thread.Panes)
            {
                yield return new NoteScopeOption
                {
                    ThreadId = thread.Id,
                    NoteId = noteId,
                    PaneId = pane.Id,
                    Label = FormatTabHeader(pane.Title, pane.Kind),
                };
            }
        }

        private static DateTimeOffset GetLatestThreadNoteActivity(WorkspaceThread thread)
        {
            return thread?.NoteEntries.Count > 0
                ? thread.NoteEntries.Max(candidate => candidate.UpdatedAt)
                : DateTimeOffset.MinValue;
        }

        private static string FormatNoteTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToLocalTime().ToString("MMM d · h:mm tt");
        }

        private static string BuildNoteEditorAutomationId(string noteId)
        {
            return string.IsNullOrWhiteSpace(noteId)
                ? "shell-thread-note-editor"
                : $"shell-thread-note-editor-{noteId}";
        }

        private static string BuildNoteTitleEditorAutomationId(string noteId)
        {
            return string.IsNullOrWhiteSpace(noteId)
                ? "shell-thread-note-title"
                : $"shell-thread-note-title-{noteId}";
        }

        private static string BuildThreadNotePreview(string notes, int maxLength = 72)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            bool lastWasWhitespace = false;
            bool truncated = false;
            foreach (char character in notes)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (builder.Length == 0 || lastWasWhitespace)
                    {
                        continue;
                    }

                    builder.Append(' ');
                    lastWasWhitespace = true;
                }
                else
                {
                    builder.Append(character);
                    lastWasWhitespace = false;
                }

                if (builder.Length >= maxLength)
                {
                    truncated = true;
                    break;
                }
            }

            string preview = builder.ToString().Trim();
            if (preview.Length == 0)
            {
                return string.Empty;
            }

            return truncated
                ? preview[..Math.Max(0, maxLength - 3)].TrimEnd() + "..."
                : preview;
        }

        private static string ResolveNoteListTitle(WorkspaceThreadNote note)
        {
            string normalizedTitle = note?.Title?.Trim();
            if (!IsSystemGeneratedNoteTitle(normalizedTitle))
            {
                return normalizedTitle;
            }

            return "Untitled note";
        }

        private static string ResolveEditableNoteTitle(WorkspaceThreadNote note)
        {
            string normalizedTitle = note?.Title?.Trim();
            return IsSystemGeneratedNoteTitle(normalizedTitle)
                ? string.Empty
                : normalizedTitle;
        }

        private static bool IsSystemGeneratedNoteTitle(string title)
        {
            string normalized = title?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (string.Equals(normalized, "Handoff", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Note", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!normalized.StartsWith("Note ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(normalized["Note ".Length..], out _);
        }

        private static string ExtractFirstNoteLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            foreach (string line in normalized.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static string RemoveFirstNoteLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            bool removed = false;
            StringBuilder builder = new();
            foreach (string line in normalized.Split('\n'))
            {
                if (!removed)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    removed = true;
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(line);
            }

            return builder.ToString().Trim();
        }

        private static IEnumerable<NativeAutomationThreadNoteState> BuildThreadNoteStates(WorkspaceThread thread)
        {
            if (thread is null)
            {
                yield break;
            }

            foreach (WorkspaceThreadNote note in thread.NoteEntries)
            {
                WorkspacePaneRecord attachedPane = thread.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, note.PaneId, StringComparison.Ordinal));
                yield return new NativeAutomationThreadNoteState
                {
                    Id = note.Id,
                    Title = note.Title,
                    Text = note.Text,
                    Preview = BuildThreadNotePreview(note.Text),
                    ProjectId = thread.Project?.Id,
                    ProjectName = thread.Project?.Name,
                    ThreadId = thread.Id,
                    ThreadName = thread.Name,
                    PaneId = note.PaneId,
                    PaneTitle = attachedPane is null ? null : FormatTabHeader(attachedPane.Title, attachedPane.Kind),
                    Selected = string.Equals(thread.SelectedNoteId, note.Id, StringComparison.Ordinal),
                    Archived = note.IsArchived,
                    UpdatedAt = note.UpdatedAt.ToString("O"),
                    ArchivedAt = note.ArchivedAt?.ToString("O"),
                };
            }
        }
    }
}
