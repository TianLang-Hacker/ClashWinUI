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
    public partial class ConnectionsViewModel : ObservableObject, IDisposable
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(200);

        private readonly LocalizedStrings _localizedStrings;
        private readonly IMihomoService _mihomoService;
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly List<ConnectionEntry> _allConnections = new();
        private DispatcherQueueTimer? _refreshTimer;
        private DispatcherQueueTimer? _filterDebounceTimer;
        private int _refreshingFlag;
        private bool _isDisposed;

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
            if (_isDisposed || _dispatcherQueue is null || _refreshTimer is not null)
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

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            StopAutoRefresh();
            StopFilterDebounce();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
            _allConnections.Clear();
            Connections.Clear();
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
            QueueFilterRefresh();
        }

        private void OnRefreshTimerTick(DispatcherQueueTimer sender, object args)
        {
            _ = RefreshConnectionsAsync(showStatus: false);
        }

        private async Task RefreshConnectionsAsync(bool showStatus)
        {
            if (_isDisposed)
            {
                return;
            }

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
            if (_isDisposed)
            {
                return;
            }

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

            SynchronizeConnections(filtered.ToList());
        }

        private void QueueFilterRefresh()
        {
            if (_isDisposed)
            {
                return;
            }

            if (_dispatcherQueue is null)
            {
                ApplyFilters();
                return;
            }

            _filterDebounceTimer ??= CreateFilterDebounceTimer();
            _filterDebounceTimer.Stop();
            _filterDebounceTimer.Start();
        }

        private DispatcherQueueTimer CreateFilterDebounceTimer()
        {
            DispatcherQueueTimer timer = _dispatcherQueue!.CreateTimer();
            timer.Interval = SearchDebounceDelay;
            timer.IsRepeating = false;
            timer.Tick += OnFilterDebounceTimerTick;
            return timer;
        }

        private void OnFilterDebounceTimerTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            ApplyFilters();
        }

        private void StopFilterDebounce()
        {
            if (_filterDebounceTimer is null)
            {
                return;
            }

            _filterDebounceTimer.Stop();
            _filterDebounceTimer.Tick -= OnFilterDebounceTimerTick;
            _filterDebounceTimer = null;
        }

        private void SynchronizeConnections(IReadOnlyList<ConnectionEntry> desired)
        {
            for (int desiredIndex = 0; desiredIndex < desired.Count; desiredIndex++)
            {
                ConnectionEntry desiredConnection = desired[desiredIndex];
                if (desiredIndex >= Connections.Count)
                {
                    Connections.Add(desiredConnection);
                    continue;
                }

                ConnectionEntry currentConnection = Connections[desiredIndex];
                if (string.Equals(currentConnection.Id, desiredConnection.Id, StringComparison.Ordinal))
                {
                    if (!ConnectionEquals(currentConnection, desiredConnection))
                    {
                        Connections[desiredIndex] = desiredConnection;
                    }

                    continue;
                }

                int existingIndex = IndexOfConnection(desiredConnection.Id, desiredIndex + 1);
                if (existingIndex >= 0)
                {
                    Connections.Move(existingIndex, desiredIndex);
                    if (!ConnectionEquals(Connections[desiredIndex], desiredConnection))
                    {
                        Connections[desiredIndex] = desiredConnection;
                    }

                    continue;
                }

                Connections.Insert(desiredIndex, desiredConnection);
            }

            while (Connections.Count > desired.Count)
            {
                Connections.RemoveAt(Connections.Count - 1);
            }
        }

        private int IndexOfConnection(string id, int startIndex)
        {
            for (int i = startIndex; i < Connections.Count; i++)
            {
                if (string.Equals(Connections[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool ConnectionEquals(ConnectionEntry left, ConnectionEntry right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.HostDisplay, right.HostDisplay, StringComparison.Ordinal)
                && string.Equals(left.TypeDisplay, right.TypeDisplay, StringComparison.Ordinal)
                && string.Equals(left.RuleDisplay, right.RuleDisplay, StringComparison.Ordinal)
                && string.Equals(left.ChainDisplay, right.ChainDisplay, StringComparison.Ordinal)
                && left.DownloadSpeed == right.DownloadSpeed
                && left.UploadSpeed == right.UploadSpeed
                && left.Download == right.Download
                && left.Upload == right.Upload
                && left.StartedAt == right.StartedAt;
        }
    }
}
