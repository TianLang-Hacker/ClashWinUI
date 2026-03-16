using ClashWinUI.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace ClashWinUI.ViewModels
{
    public partial class ConnectionsViewModel : ObservableObject
    {
        private readonly LocalizedStrings _localizedStrings;

        [ObservableProperty]
        public partial string Title { get; set; }

        public ConnectionsViewModel(LocalizedStrings localizedStrings)
        {
            _localizedStrings = localizedStrings;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageConnections"];
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageConnections"];
            }
        }
    }
}
