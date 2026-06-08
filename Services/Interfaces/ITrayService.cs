using System;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface ITrayService : IDisposable
    {
        bool IsInitialized { get; }

        void Initialize(
            Func<string, Task> showMainWindowAsyncAction,
            Func<Task> restartApplicationAsyncAction,
            Func<Task> exitApplicationAsyncAction);
        void Show();
        void Shutdown();
    }
}
