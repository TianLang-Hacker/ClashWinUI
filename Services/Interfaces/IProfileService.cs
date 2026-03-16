using ClashWinUI.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IProfileService
    {
        string ProfilesDirectory { get; }
        IReadOnlyList<ProfileItem> GetProfiles();
        ProfileItem? GetActiveProfile();
        Task<ProfileItem> AddOrUpdateFromSubscriptionAsync(string subscriptionUrl, CancellationToken cancellationToken = default);
        Task<ProfileItem> ImportLocalFileAsync(string localFilePath, CancellationToken cancellationToken = default);
        bool SetActiveProfile(string profileId);
        bool DeleteProfile(string profileId);
    }
}
