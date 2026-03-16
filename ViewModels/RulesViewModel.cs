using ClashWinUI.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace ClashWinUI.ViewModels
{
    public partial class RulesViewModel : ObservableObject
    {
        private readonly LocalizedStrings _localizedStrings;

        [ObservableProperty]
        public partial string Title { get; set; }

        public RulesViewModel(LocalizedStrings localizedStrings)
        {
            _localizedStrings = localizedStrings;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageRules"];
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageRules"];
            }
        }
    }
}
