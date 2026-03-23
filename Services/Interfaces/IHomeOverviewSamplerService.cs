using ClashWinUI.Models;
using System;

namespace ClashWinUI.Services.Interfaces
{
    public interface IHomeOverviewSamplerService : IDisposable
    {
        event EventHandler? StateChanged;

        HomeOverviewState GetState();

        void FlushState();

        void Start();
    }
}
