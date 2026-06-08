using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class ProxiesPage : Page, IShellFreezablePage
    {
        private static readonly TimeSpan ExpanderContentAnimationDuration = TimeSpan.FromMilliseconds(160);
        private static readonly TimeSpan CollapseMembersDelay = TimeSpan.FromMilliseconds(180);

        private ProxiesViewModel? _viewModel;

        public ProxiesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            ProxiesViewModel viewModel = _viewModel ?? ResolveViewModel();
            if (_viewModel is null)
            {
                _viewModel = viewModel;
                DataContext = viewModel;
            }

            await viewModel.ActivateAsync();

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _viewModel?.Deactivate();
            base.OnNavigatedFrom(e);
        }

        private void ReleaseViewModel()
        {
            if (_viewModel is null)
            {
                DataContext = null;
                return;
            }

            _viewModel.StopWatchingRuntimeChanges();
            _viewModel.Dispose();
            _viewModel = null;
            DataContext = null;
        }

        private static ProxiesViewModel ResolveViewModel()
        {
            return ((App)Application.Current).GetRequiredService<ProxiesViewModel>();
        }

        public void PrepareForShellFreeze()
        {
            ReleaseViewModel();
        }

        private void ProxyGroupExpander_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ProxyGroup group }
                && group.IsExpanded
                && group.VisibleMembers.Count == 0)
            {
                group.BeginExpandMembers();
            }
        }

        private void ProxyGroupExpander_Expanded(object sender, EventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ProxyGroup group } expander)
            {
                return;
            }

            group.BeginExpandMembers();
            AnimateExpanderContent(expander);
        }

        private async void ProxyGroupExpander_Collapsed(object sender, EventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ProxyGroup group })
            {
                return;
            }

            await Task.Delay(CollapseMembersDelay);
            group.CollapseMembersAfterAnimation();
        }

        private static void AnimateExpanderContent(DependencyObject expander)
        {
            if (FindNamedElement(expander, "ProxyGroupContentHost") is not UIElement content)
            {
                return;
            }

            AnimationBuilder.Create()
                .Opacity(to: 1, from: 0, duration: ExpanderContentAnimationDuration)
                .Translation(Axis.Y, to: 0, from: -8, duration: ExpanderContentAnimationDuration)
                .Start(content);
        }

        private static FrameworkElement? FindNamedElement(DependencyObject root, string name)
        {
            if (root is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                FrameworkElement? match = FindNamedElement(child, name);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
