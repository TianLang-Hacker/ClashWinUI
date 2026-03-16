using ClashWinUI.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace ClashWinUI.ViewModels
{
    public partial class HomeViewModel : ObservableObject
    {
        private readonly LocalizedStrings _localizedStrings;

        [ObservableProperty]
        public partial string Title { get; set; }

        public HomeViewModel(LocalizedStrings localizedStrings)
        {
            _localizedStrings = localizedStrings;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageOverview"];
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageOverview"];
            }
        }
    }
}
