using ClashWinUI.Models;
using System;

namespace ClashWinUI.Services.Interfaces
{
    public interface IAppSettingsService
    {
        event EventHandler? SettingsChanged;

        CloseBehavior CloseBehavior { get; set; }
    }
}
