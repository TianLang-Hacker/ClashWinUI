using ClashWinUI.Models;
using System.Collections.Generic;

namespace ClashWinUI.Services.Interfaces
{
    public interface IConfigService
    {
        event System.EventHandler? ConfigurationChanged;

        ProfileConfigWorkspace GetWorkspace(ProfileItem profile);
        ProfileConfigWorkspace EnsureWorkspace(ProfileItem profile);
        MixinSettings LoadMixin(ProfileItem profile);
        void SaveMixin(ProfileItem profile, MixinSettings settings);
        string BuildRuntime(ProfileItem profile);
        string GetRuntimePath(ProfileItem profile);
        IReadOnlyList<RuntimeRuleItem> GetRuntimeRules(ProfileItem profile);
        void SetRuleEnabled(ProfileItem profile, string stableId, bool isEnabled);
    }
}
