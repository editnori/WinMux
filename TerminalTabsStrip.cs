using System;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SelfContainedDeployment
{
    public sealed class TerminalTabsStrip : UserControl
    {
        private readonly TabView tabView;

        public TerminalTabsStrip()
        {
            tabView = new TabView
            {
                IsAddTabButtonVisible = true,
                TabWidthMode = TabViewWidthMode.SizeToContent
            };

            tabView.AddTabButtonClick += TabView_AddTabButtonClick;
            tabView.TabCloseRequested += TabView_TabCloseRequested;
            tabView.SelectionChanged += TabView_SelectionChanged;

            Content = tabView;
            AttachModel(Model);
            RebuildTabs();
        }

        public TerminalTabsController Model { get; private set; } = new();

        public event EventHandler<TerminalTabEventArgs> TabAdded;
        public event EventHandler<TerminalTabEventArgs> TabClosed;
        public event EventHandler<TerminalTabEventArgs> TabRenamedRequested;
        public event EventHandler<TerminalTabEventArgs> ActiveTabChanged;

        public void SetModel(TerminalTabsController controller)
        {
            DetachModel(Model);
            Model = controller ?? new TerminalTabsController();
            AttachModel(Model);
            RebuildTabs();
        }

        private void AttachModel(TerminalTabsController controller)
        {
            controller.Tabs.CollectionChanged += Tabs_CollectionChanged;
            controller.PropertyChanged += Controller_PropertyChanged;

            foreach (var tab in controller.Tabs)
            {
                tab.PropertyChanged += Tab_PropertyChanged;
            }
        }

        private void DetachModel(TerminalTabsController controller)
        {
            controller.Tabs.CollectionChanged -= Tabs_CollectionChanged;
            controller.PropertyChanged -= Controller_PropertyChanged;

            foreach (var tab in controller.Tabs)
            {
                tab.PropertyChanged -= Tab_PropertyChanged;
            }
        }

        private void Tabs_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (TerminalTabItem tab in e.NewItems)
                {
                    tab.PropertyChanged += Tab_PropertyChanged;
                }
            }

            if (e.OldItems is not null)
            {
                foreach (TerminalTabItem tab in e.OldItems)
                {
                    tab.PropertyChanged -= Tab_PropertyChanged;
                }
            }

            RebuildTabs();
        }

        private void Controller_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TerminalTabsController.SelectedTab))
            {
                RebuildTabs();
            }
        }

        private void Tab_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RebuildTabs();
        }

        private void RebuildTabs()
        {
            tabView.TabItems.Clear();

            foreach (var tab in Model.Tabs)
            {
                var tabViewItem = new TabViewItem
                {
                    Header = CreateHeader(tab),
                    IsClosable = tab.CanClose,
                    Tag = tab
                };

                tabView.TabItems.Add(tabViewItem);

                if (ReferenceEquals(tab, Model.SelectedTab))
                {
                    tabView.SelectedItem = tabViewItem;
                }
            }
        }

        private UIElement CreateHeader(TerminalTabItem tab)
        {
            var title = new TextBlock
            {
                Text = tab.Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var subtitle = new TextBlock
            {
                Text = tab.Subtitle,
                Opacity = 0.8,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(title);
            stack.Children.Add(subtitle);

            var header = new Grid
            {
                MinWidth = 160
            };

            header.DoubleTapped += (_, _) =>
            {
                if (tab.CanRename)
                {
                    TabRenamedRequested?.Invoke(this, new TerminalTabEventArgs(tab));
                }
            };

            header.Children.Add(stack);
            return header;
        }

        private void TabView_AddTabButtonClick(TabView sender, object args)
        {
            var tab = Model.AddNewTab("Terminal " + (Model.Tabs.Count + 1), "New session");
            RebuildTabs();
            TabAdded?.Invoke(this, new TerminalTabEventArgs(tab));
        }

        private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is not TabViewItem item || item.Tag is not TerminalTabItem tab)
            {
                return;
            }

            Model.CloseTab(tab);
            RebuildTabs();
            TabClosed?.Invoke(this, new TerminalTabEventArgs(tab));
        }

        private void TabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (tabView.SelectedItem is not TabViewItem item || item.Tag is not TerminalTabItem tab)
            {
                return;
            }

            Model.SelectedTab = tab;
            ActiveTabChanged?.Invoke(this, new TerminalTabEventArgs(tab));
        }
    }
}
