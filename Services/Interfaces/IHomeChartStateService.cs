using ClashWinUI.Models;

namespace ClashWinUI.Services.Interfaces
{
    public interface IHomeChartStateService
    {
        HomeChartState GetState();
        void Save(HomeChartState state);
    }
}
