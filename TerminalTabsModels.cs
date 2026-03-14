using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SelfContainedDeployment
{
    public sealed class TerminalTabItem : ObservableTabObject
    {
        private string title;
        private string subtitle;
        private bool isDirty;
        private bool canClose;
        private bool canRename;

        public TerminalTabItem(string title, string subtitle = "")
        {
            this.title = title;
            this.subtitle = subtitle;
            canClose = true;
            canRename = true;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Title
        {
            get => title;
            set => SetProperty(ref title, value);
        }

        public string Subtitle
        {
            get => subtitle;
            set => SetProperty(ref subtitle, value);
        }

        public bool IsDirty
        {
            get => isDirty;
            set => SetProperty(ref isDirty, value);
        }

        public bool CanClose
        {
            get => canClose;
            set => SetProperty(ref canClose, value);
        }

        public bool CanRename
        {
            get => canRename;
            set => SetProperty(ref canRename, value);
        }
    }

    public sealed class TerminalTabsController : ObservableTabObject
    {
        private TerminalTabItem selectedTab;
        private int untitledCounter = 1;

        public ObservableCollection<TerminalTabItem> Tabs { get; } = new();

        public TerminalTabItem SelectedTab
        {
            get => selectedTab;
            set => SetProperty(ref selectedTab, value);
        }

        public TerminalTabItem AddNewTab(string title = null, string subtitle = "")
        {
            var nextTitle = string.IsNullOrWhiteSpace(title) ? "Untitled " + untitledCounter++ : title;
            var tab = new TerminalTabItem(nextTitle, subtitle);
            Tabs.Add(tab);
            SelectedTab = tab;
            return tab;
        }

        public void CloseTab(TerminalTabItem tab)
        {
            if (tab is null || !Tabs.Contains(tab))
            {
                return;
            }

            var closingIndex = Tabs.IndexOf(tab);
            Tabs.Remove(tab);

            if (Tabs.Count == 0)
            {
                SelectedTab = null;
                return;
            }

            if (SelectedTab == tab)
            {
                var nextIndex = Math.Min(closingIndex, Tabs.Count - 1);
                SelectedTab = Tabs[nextIndex];
            }
        }

        public void RenameTab(TerminalTabItem tab, string title)
        {
            if (tab is null || string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            tab.Title = title.Trim();
        }
    }

    public abstract class ObservableTabObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
