using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashWinUI.Views
{
    public sealed partial class WelcomeWizardControl : UserControl
    {
        private static readonly TimeSpan PageTransitionDuration = TimeSpan.FromMilliseconds(260);
        private const double PageTransitionOffset = 72d;
        private const double PageTransitionStartScale = 0.98d;

        private readonly WelcomeWizardViewModel _viewModel;
        private CancellationTokenSource? _pageTransitionCancellation;
        private int _displayedPageIndex;
        private bool _isPageTransitionRunning;

        public WelcomeWizardControl(WelcomeWizardViewModel viewModel)
        {
            _viewModel = viewModel;
            Helpers.StartupTrace.Write("WelcomeWizardControl ctor: before InitializeComponent");
            InitializeComponent();
            Helpers.StartupTrace.Write("WelcomeWizardControl ctor: after InitializeComponent");
            DataContext = _viewModel;
            _displayedPageIndex = _viewModel.CurrentPageIndex;
            SetVisiblePage(_displayedPageIndex);
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Unloaded += OnUnloaded;
            Helpers.StartupTrace.Write("WelcomeWizardControl ctor: DataContext assigned");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Unloaded -= OnUnloaded;
            CancelPageTransition();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(WelcomeWizardViewModel.CurrentPageIndex))
            {
                return;
            }

            if (_isPageTransitionRunning)
            {
                return;
            }

            int targetPageIndex = _viewModel.CurrentPageIndex;
            if (targetPageIndex == _displayedPageIndex)
            {
                return;
            }

            _ = RunPageIndexTransitionAsync(_displayedPageIndex, targetPageIndex);
        }

        private async void BackButton_Click(object sender, RoutedEventArgs e)
        {
            await RunNavigationTransitionAsync(
                isForward: false,
                canNavigate: () => _viewModel.CanGoBack,
                navigate: _viewModel.TryGoBack);
        }

        private async void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            await RunNavigationTransitionAsync(
                isForward: true,
                canNavigate: () => _viewModel.CanSkip,
                navigate: _viewModel.TrySkip);
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await RunNavigationTransitionAsync(
                isForward: true,
                canNavigate: () => _viewModel.CanGoNext,
                navigate: _viewModel.TryGoNext);
        }

        private async Task RunNavigationTransitionAsync(bool isForward, Func<bool> canNavigate, Func<bool> navigate)
        {
            if (_isPageTransitionRunning || !canNavigate())
            {
                return;
            }

            _isPageTransitionRunning = true;
            CancelPageTransition(resetRunningFlag: false);
            var transitionCancellation = new CancellationTokenSource();
            _pageTransitionCancellation = transitionCancellation;

            PageContentHost.IsHitTestVisible = false;
            NavigationButtonsHost.IsHitTestVisible = false;
            int fromPageIndex = _viewModel.CurrentPageIndex;
            FrameworkElement currentPanel = GetPagePanel(fromPageIndex);
            SetVisiblePage(fromPageIndex);

            try
            {
                await AnimatePageOutAsync(currentPanel, isForward, transitionCancellation.Token);

                bool navigated = navigate();
                int toPageIndex = _viewModel.CurrentPageIndex;
                if (!navigated || toPageIndex == fromPageIndex)
                {
                    SetVisiblePage(fromPageIndex);
                    await AnimatePageInAsync(currentPanel, isForward, transitionCancellation.Token);
                    return;
                }

                FrameworkElement nextPanel = GetPagePanel(toPageIndex);
                currentPanel.Visibility = Visibility.Collapsed;
                ResetPagePanel(currentPanel);
                nextPanel.Visibility = Visibility.Visible;
                nextPanel.UpdateLayout();
                await AnimatePageInAsync(nextPanel, isForward, transitionCancellation.Token);
                _displayedPageIndex = toPageIndex;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Helpers.StartupTrace.WriteException("Welcome navigation transition failed", ex);
                SetVisiblePage(_viewModel.CurrentPageIndex);
                _displayedPageIndex = _viewModel.CurrentPageIndex;
            }
            finally
            {
                if (_pageTransitionCancellation == transitionCancellation)
                {
                    _pageTransitionCancellation = null;
                }

                transitionCancellation.Dispose();
                ResetAllPagePanels();
                SetVisiblePage(_viewModel.CurrentPageIndex);
                _displayedPageIndex = _viewModel.CurrentPageIndex;
                PageContentHost.IsHitTestVisible = true;
                NavigationButtonsHost.IsHitTestVisible = true;
                _isPageTransitionRunning = false;
            }
        }

        private async Task RunPageIndexTransitionAsync(int fromPageIndex, int toPageIndex)
        {
            _isPageTransitionRunning = true;
            CancelPageTransition(resetRunningFlag: false);
            var transitionCancellation = new CancellationTokenSource();
            _pageTransitionCancellation = transitionCancellation;

            PageContentHost.IsHitTestVisible = false;
            NavigationButtonsHost.IsHitTestVisible = false;
            bool isForward = toPageIndex > fromPageIndex;
            FrameworkElement currentPanel = GetPagePanel(fromPageIndex);
            SetVisiblePage(fromPageIndex);

            try
            {
                await AnimatePageOutAsync(currentPanel, isForward, transitionCancellation.Token);

                FrameworkElement nextPanel = GetPagePanel(toPageIndex);
                currentPanel.Visibility = Visibility.Collapsed;
                ResetPagePanel(currentPanel);

                nextPanel.Visibility = Visibility.Visible;
                nextPanel.UpdateLayout();
                await AnimatePageInAsync(nextPanel, isForward, transitionCancellation.Token);
                _displayedPageIndex = toPageIndex;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Helpers.StartupTrace.WriteException("Welcome page transition failed", ex);
            }
            finally
            {
                if (_pageTransitionCancellation == transitionCancellation)
                {
                    _pageTransitionCancellation = null;
                }

                transitionCancellation.Dispose();
                ResetAllPagePanels();
                SetVisiblePage(_viewModel.CurrentPageIndex);
                _displayedPageIndex = _viewModel.CurrentPageIndex;
                PageContentHost.IsHitTestVisible = true;
                NavigationButtonsHost.IsHitTestVisible = true;
                _isPageTransitionRunning = false;
            }
        }

        private async Task AnimatePageOutAsync(FrameworkElement pagePanel, bool isForward, CancellationToken cancellationToken)
        {
            double toOffset = isForward ? -PageTransitionOffset : PageTransitionOffset;
            PreparePagePanelForAnimation(pagePanel);
            await RunStoryboardAsync(
                pagePanel,
                fromOpacity: 1,
                toOpacity: 0,
                fromOffset: 0,
                toOffset: toOffset,
                fromScale: 1,
                toScale: PageTransitionStartScale,
                cancellationToken);
        }

        private async Task AnimatePageInAsync(FrameworkElement pagePanel, bool isForward, CancellationToken cancellationToken)
        {
            double fromOffset = isForward ? PageTransitionOffset : -PageTransitionOffset;
            PreparePagePanelForAnimation(pagePanel);
            pagePanel.Opacity = 0;
            if (pagePanel.RenderTransform is CompositeTransform transform)
            {
                transform.TranslateX = fromOffset;
                transform.ScaleX = PageTransitionStartScale;
                transform.ScaleY = PageTransitionStartScale;
            }

            await RunStoryboardAsync(
                pagePanel,
                fromOpacity: 0,
                toOpacity: 1,
                fromOffset: fromOffset,
                toOffset: 0,
                fromScale: PageTransitionStartScale,
                toScale: 1,
                cancellationToken);
        }

        private static async Task RunStoryboardAsync(
            FrameworkElement pagePanel,
            double fromOpacity,
            double toOpacity,
            double fromOffset,
            double toOffset,
            double fromScale,
            double toScale,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var storyboard = new Storyboard();
            storyboard.Children.Add(CreateDoubleAnimation(pagePanel, "Opacity", fromOpacity, toOpacity));
            storyboard.Children.Add(CreateDoubleAnimation(pagePanel, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)", fromOffset, toOffset));
            storyboard.Children.Add(CreateDoubleAnimation(pagePanel, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", fromScale, toScale));
            storyboard.Children.Add(CreateDoubleAnimation(pagePanel, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", fromScale, toScale));

            void OnCompleted(object? sender, object e)
            {
                storyboard.Completed -= OnCompleted;
                completion.TrySetResult(true);
            }

            storyboard.Completed += OnCompleted;
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                storyboard.Completed -= OnCompleted;
                storyboard.Stop();
                completion.TrySetCanceled(cancellationToken);
            });

            storyboard.Begin();
            await completion.Task.ConfigureAwait(true);
        }

        private static DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            string targetProperty,
            double from,
            double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = PageTransitionDuration,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, targetProperty);
            return animation;
        }

        private void PreparePagePanelForAnimation(FrameworkElement pagePanel)
        {
            pagePanel.Visibility = Visibility.Visible;
            pagePanel.IsHitTestVisible = false;
            pagePanel.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            pagePanel.RenderTransform = new CompositeTransform();
            pagePanel.Opacity = 1;
        }

        private void SetVisiblePage(int pageIndex)
        {
            foreach (FrameworkElement pagePanel in GetPagePanels())
            {
                pagePanel.Visibility = Visibility.Collapsed;
                ResetPagePanel(pagePanel);
            }

            FrameworkElement visiblePanel = GetPagePanel(pageIndex);
            visiblePanel.Visibility = Visibility.Visible;
            visiblePanel.IsHitTestVisible = true;
        }

        private void ResetAllPagePanels()
        {
            foreach (FrameworkElement pagePanel in GetPagePanels())
            {
                ResetPagePanel(pagePanel);
            }
        }

        private void ResetPagePanel(FrameworkElement pagePanel)
        {
            pagePanel.Opacity = 1;
            if (pagePanel.RenderTransform is CompositeTransform transform)
            {
                transform.TranslateX = 0;
                transform.ScaleX = 1;
                transform.ScaleY = 1;
            }

            pagePanel.IsHitTestVisible = pagePanel.Visibility == Visibility.Visible;
        }

        private FrameworkElement GetPagePanel(int pageIndex)
        {
            return pageIndex switch
            {
                0 => IntroPagePanel,
                1 => ThemePagePanel,
                2 => KernelPagePanel,
                3 => ImportPagePanel,
                4 => DownloadPagePanel,
                _ => CompletePagePanel,
            };
        }

        private FrameworkElement[] GetPagePanels()
        {
            return
            [
                IntroPagePanel,
                ThemePagePanel,
                KernelPagePanel,
                ImportPagePanel,
                DownloadPagePanel,
                CompletePagePanel,
            ];
        }

        private void CancelPageTransition(bool resetRunningFlag = true)
        {
            CancellationTokenSource? transitionCancellation = _pageTransitionCancellation;
            _pageTransitionCancellation = null;
            transitionCancellation?.Cancel();
            transitionCancellation?.Dispose();
            ResetAllPagePanels();
            PageContentHost.IsHitTestVisible = true;
            NavigationButtonsHost.IsHitTestVisible = true;
            if (resetRunningFlag)
            {
                _isPageTransitionRunning = false;
            }
        }

        private void CustomKernelRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            _viewModel.IsOnlineKernelSelected = false;
        }

        private async void BrowseKernelPathButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");

            if (Application.Current is App app && app.ActiveWindow is not null)
            {
                nint hwnd = WindowNative.GetWindowHandle(app.ActiveWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            _viewModel.CustomKernelPathInput = file.Path;
            _viewModel.IsOnlineKernelSelected = false;
        }

        private async void ImportLocalFileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add("*");

            if (Application.Current is App app && app.ActiveWindow is not null)
            {
                nint hwnd = WindowNative.GetWindowHandle(app.ActiveWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            await _viewModel.ImportLocalFileAsync(file.Path);
        }
    }
}
