using ClashWinUI.Models;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClashWinUI.Serialization
{
    internal sealed class AppSettingsState
    {
        public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.MinimizeToTray;

        public bool ProxyGroupsExpandedByDefault { get; set; } = false;
    }

    internal sealed class KernelPathSettingsState
    {
        public string? CustomKernelPath { get; set; }
    }

    internal sealed class ProfileStoreState
    {
        public string? ActiveProfileId { get; set; }
        public List<ProfileItem> Profiles { get; set; } = new();
    }

    internal sealed class RulesOverrideState
    {
        public List<string> DisabledRuleIds { get; set; } = new();
    }

    internal sealed class MihomoApplyConfigPayload
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("force")]
        public bool Force { get; set; } = true;
    }

    internal sealed class MihomoProxySelectionPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
    [JsonSerializable(typeof(AppSettingsState))]
    [JsonSerializable(typeof(KernelPathSettingsState))]
    [JsonSerializable(typeof(ProfileStoreState))]
    [JsonSerializable(typeof(RulesOverrideState))]
    [JsonSerializable(typeof(MihomoApplyConfigPayload))]
    [JsonSerializable(typeof(MihomoProxySelectionPayload))]
    internal partial class ClashJsonContext : JsonSerializerContext
    {
    }
}
