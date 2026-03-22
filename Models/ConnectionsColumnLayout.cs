using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClashWinUI.Models
{
    public sealed class ConnectionsColumnLayout : INotifyPropertyChanged
    {
        private GridLength _closeColumnWidth = new(72);
        private GridLength _hostColumnWidth = new(170);
        private GridLength _typeColumnWidth = new(130);
        private GridLength _ruleColumnWidth = new(100);
        private GridLength _chainColumnWidth = new(240);
        private GridLength _downloadSpeedColumnWidth = new(110);
        private GridLength _uploadSpeedColumnWidth = new(110);
        private GridLength _downloadColumnWidth = new(100);
        private GridLength _uploadColumnWidth = new(100);
        private GridLength _durationColumnWidth = new(120);

        public event PropertyChangedEventHandler? PropertyChanged;

        public GridLength CloseColumnWidth
        {
            get => _closeColumnWidth;
            set => SetProperty(ref _closeColumnWidth, value);
        }

        public GridLength HostColumnWidth
        {
            get => _hostColumnWidth;
            set => SetProperty(ref _hostColumnWidth, value);
        }

        public GridLength TypeColumnWidth
        {
            get => _typeColumnWidth;
            set => SetProperty(ref _typeColumnWidth, value);
        }

        public GridLength RuleColumnWidth
        {
            get => _ruleColumnWidth;
            set => SetProperty(ref _ruleColumnWidth, value);
        }

        public GridLength ChainColumnWidth
        {
            get => _chainColumnWidth;
            set => SetProperty(ref _chainColumnWidth, value);
        }

        public GridLength DownloadSpeedColumnWidth
        {
            get => _downloadSpeedColumnWidth;
            set => SetProperty(ref _downloadSpeedColumnWidth, value);
        }

        public GridLength UploadSpeedColumnWidth
        {
            get => _uploadSpeedColumnWidth;
            set => SetProperty(ref _uploadSpeedColumnWidth, value);
        }

        public GridLength DownloadColumnWidth
        {
            get => _downloadColumnWidth;
            set => SetProperty(ref _downloadColumnWidth, value);
        }

        public GridLength UploadColumnWidth
        {
            get => _uploadColumnWidth;
            set => SetProperty(ref _uploadColumnWidth, value);
        }

        public GridLength DurationColumnWidth
        {
            get => _durationColumnWidth;
            set => SetProperty(ref _durationColumnWidth, value);
        }

        private void SetProperty(ref GridLength field, GridLength value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
