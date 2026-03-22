using CommunityToolkit.Mvvm.ComponentModel;

namespace ClashWinUI.Models
{
    public enum RuleActionKind
    {
        Direct = 0,
        Proxy = 1,
        Reject = 2,
        Pass = 3,
        Other = 4,
    }

    public partial class RuntimeRuleItem : ObservableObject
    {
        [ObservableProperty]
        public partial string StableId { get; set; } = string.Empty;

        [ObservableProperty]
        public partial int Index { get; set; }

        [ObservableProperty]
        public partial string MatcherType { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MatcherTypeDisplay { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MatcherValue { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MatcherValueDisplay { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string RawRuleText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial RuleActionKind ActionKind { get; set; }

        [ObservableProperty]
        public partial string ActionKindDisplay { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ActionTargetRaw { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ActionTargetDisplay { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsEnabled { get; set; } = true;

        [ObservableProperty]
        public partial bool IsApplying { get; set; }

        public bool IsToggleSynchronizing { get; set; }

        public string IndexDisplay => $"{Index}.";

        public bool CanToggle => !IsApplying;

        public string SearchText => string.Join(
            " ",
            MatcherTypeDisplay,
            MatcherValueDisplay,
            RawRuleText,
            ActionKindDisplay,
            ActionTargetDisplay);

        partial void OnIndexChanged(int value)
        {
            OnPropertyChanged(nameof(IndexDisplay));
        }

        partial void OnMatcherTypeDisplayChanged(string value)
        {
            OnPropertyChanged(nameof(SearchText));
        }

        partial void OnMatcherValueDisplayChanged(string value)
        {
            OnPropertyChanged(nameof(SearchText));
        }

        partial void OnRawRuleTextChanged(string value)
        {
            OnPropertyChanged(nameof(SearchText));
        }

        partial void OnActionKindDisplayChanged(string value)
        {
            OnPropertyChanged(nameof(SearchText));
        }

        partial void OnActionTargetDisplayChanged(string value)
        {
            OnPropertyChanged(nameof(SearchText));
        }

        partial void OnIsApplyingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanToggle));
        }
    }
}
