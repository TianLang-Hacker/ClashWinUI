using Microsoft.UI.Xaml.Controls;

namespace ClashWinUI.Services.Interfaces
{
    public interface INavigationService
    {
        void Initialize(Frame frame);
        void Navigate(string routeKey);
    }
}
