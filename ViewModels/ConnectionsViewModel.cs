using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class ConnectionsViewModel : ObservableObject
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

        private readonly LocalizedStrings _localizedStrings;
        private readonly IMihomoService _mihomoService;
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly List<ConnectionEntry> _allConnections = new();
        private DispatcherQueueTimer? _refreshTimer;
        private int _refreshingFlag;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial int ActiveConnectionCount { get; set; }

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        public partial string SearchKeyword { get; set; }

        public ObservableCollection<ConnectionEntry> Connections { get; } = new();

        public ConnectionsViewModel(LocalizedStrings localizedStrings, IMihomoService mihomoService)
        {
            _localizedStrings = localizedStrings;
            _mihomoService = mihomoService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageConnections"];
            StatusMessage = string.Empty;
            SearchKeyword = string.Empty;
        }

        public Task InitializeAsync()
        {
            return RefreshConnectionsAsync(showStatus: true);
        }

        public void StartAutoRefresh()
        {
            if (_dispatcherQueue is null || _refreshTimer is not null)
            {
                return;
            }

            _refreshTimer = _dispatcherQueue.CreateTimer();
            _refreshTimer.Interval = RefreshInterval;
            _refreshTimer.IsRepeating = true;
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
        }

        public void StopAutoRefresh()
        {
            if (_refreshTimer is null)
            {
                return;
            }

            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
            _refreshTimer = null;
        }

        [RelayCommand]
        private Task RefreshAsync()
        {
            return RefreshConnectionsAsync(showStatus: true);
        }

        [RelayCommand]
        private async Task CloseConnectionAsync(ConnectionEntry? connection)
        {
            if (connection is null || string.IsNullOrWhiteSpace(connection.Id))
            {
                return;
            }

            bool closed = await _mihomoService.CloseConnectionAsync(connection.Id);
            if (!closed)
            {
                StatusMessage = string.Format(_localizedStrings["ConnectionsStatusCloseFailed"], connection.HostDisplay);
                return;
            }

            StatusMessage = string.Format(_localizedStrings["ConnectionsStatusClosed"], connection.HostDisplay);
            await RefreshConnectionsAsync(showStatus: false);
        }

        partial void OnIsLoadingChanged(bool value)
        {
            RefreshCommand.NotifyCanExecuteChanged();
            CloseConnectionCommand.NotifyCanExecuteChanged();
        }

        partial void OnSearchKeywordChanged(string value)
        {
            ApplyFilters();
        }

        private void OnRefreshTimerTick(DispatcherQueueTimer sender, object args)
        {
            _ = RefreshConnectionsAsync(showStatus: false);
        }

        private async Task RefreshConnectionsAsync(bool showStatus)
        {
            if (Interlocked.Exchange(ref _refreshingFlag, 1) == 1)
            {
                return;
            }

            IsLoading = true;
            try
            {
                IReadOnlyList<ConnectionEntry> connections = await _mihomoService.GetConnectionsAsync();
                _allConnections.Clear();
                _allConnections.AddRange(connections.OrderByDescending(item => item.StartedAt ?? DateTimeOffset.MinValue));

                ApplyFilters();
                ActiveConnectionCount = _allConnections.Count;
                if (showStatus)
                {
                    StatusMessage = _allConnections.Count == 0
                        ? _localizedStrings["ConnectionsStatusEmpty"]
                        : string.Format(_localizedStrings["ConnectionsStatusLoaded"], _allConnections.Count);
                }
            }
            catch (Exception ex)
            {
                if (showStatus)
                {
                    StatusMessage = string.Format(_localizedStrings["ConnectionsStatusLoadFailed"], ex.Message);
                }
            }
            finally
            {
                IsLoading = false;
                Interlocked.Exchange(ref _refreshingFlag, 0);
            }
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageConnections"];
                if (ActiveConnectionCount > 0)
                {
                    StatusMessage = string.Format(_localizedStrings["ConnectionsStatusLoaded"], ActiveConnectionCount);
                }
                else if (string.IsNullOrWhiteSpace(StatusMessage))
                {
                    StatusMessage = _localizedStrings["ConnectionsStatusEmpty"];
                }
            }
        }

        private void ApplyFilters()
        {
            string[] keywords = (SearchKeyword ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            IEnumerable<ConnectionEntry> filtered = _allConnections;
            if (keywords.Length > 0)
            {
                filtered = filtered.Where(connection =>
                {
                    string host = connection.HostDisplay ?? string.Empty;
                    return keywords.Any(keyword => host.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                });
            }

            Connections.Clear();
            foreach (ConnectionEntry connection in filtered)
            {
                Connections.Add(connection);
            }
        }
    }
}
