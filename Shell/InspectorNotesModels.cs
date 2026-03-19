using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;

namespace SelfContainedDeployment
{
    internal sealed class NoteDraftState
    {
        public string EditableTitle { get; set; } = string.Empty;

        public string Text { get; set; }

        public bool Dirty { get; set; }
    }

    internal sealed class InspectorNoteCardItem
    {
        public string NoteId { get; init; }

        public string ThreadId { get; init; }

        public string Title { get; init; }

        public string EditableTitle { get; init; }

        public string TitlePlaceholderText { get; init; }

        public string TitleEditorAutomationId { get; init; }

        public string Meta { get; init; }

        public string ScopeButtonLabel { get; init; }

        public string ScopeToolTip { get; init; }

        public string ArchiveButtonLabel { get; init; }

        public string ArchiveToolTip { get; init; }

        public string DeleteToolTip { get; init; }

        public string PlaceholderText { get; init; }

        public string EditorAutomationId { get; init; }

        public string Text { get; init; }

        public string StatusText { get; init; }

        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;

        public string TimestampText { get; init; }

        public Brush AccentBrush { get; init; }

        public Brush CardBackground { get; init; }

        public Brush CardBorderBrush { get; init; }

        public Visibility ArchiveButtonVisibility { get; init; }

        public bool IsArchived { get; init; }

        public bool IsSelected { get; init; }
    }

    internal sealed class InspectorNoteGroupItem
    {
        public string ThreadId { get; init; }

        public string Title { get; init; }

        public string Meta { get; init; }

        public Brush HeaderAccentBrush { get; init; }

        public Visibility HeaderVisibility { get; init; }

        public string ArchivedToggleText { get; init; }

        public Visibility ArchivedSectionVisibility { get; init; }

        public Visibility ArchivedItemsVisibility { get; init; }

        public List<InspectorNoteCardItem> ActiveNotes { get; init; } = new();

        public List<InspectorNoteCardItem> ArchivedNotes { get; init; } = new();
    }

    internal sealed class NoteScopeOption
    {
        public string ThreadId { get; init; }

        public string NoteId { get; init; }

        public string PaneId { get; init; }

        public string Label { get; init; }
    }
}
