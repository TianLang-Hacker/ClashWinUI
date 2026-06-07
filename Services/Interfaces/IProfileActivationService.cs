using ClashWinUI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IProfileActivationService
    {
        Task<ProfileActivationResult> ActivateAsync(ProfileItem profile, CancellationToken cancellationToken = default);
    }
}
