using System;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface ITrayService : IDisposable
    {
        bool IsInitialized { get; }

        void Initialize(Action showMainWindowAction, Func<Task> exitApplicationAsyncAction);
        void Show();
        void Shutdown();
    }
}
