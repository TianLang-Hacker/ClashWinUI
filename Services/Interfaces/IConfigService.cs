
using ClashWinUI.Models;

namespace ClashWinUI.Services.Interfaces
{
    public interface IConfigService
    {
        ProfileConfigWorkspace GetWorkspace(ProfileItem profile);
        ProfileConfigWorkspace EnsureWorkspace(ProfileItem profile);
        MixinSettings LoadMixin(ProfileItem profile);
        void SaveMixin(ProfileItem profile, MixinSettings settings);
        string BuildRuntime(ProfileItem profile);
        string GetRuntimePath(ProfileItem profile);
    }
}
