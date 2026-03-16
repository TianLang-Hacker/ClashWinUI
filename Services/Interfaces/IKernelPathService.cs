namespace ClashWinUI.Services.Interfaces
{
    public interface IKernelPathService
    {
        string DefaultKernelPath { get; }
        string? CustomKernelPath { get; }

        string ResolveKernelPath();
        void SetCustomKernelPath(string? path);
    }
}
