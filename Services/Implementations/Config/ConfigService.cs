
using ClashWinUI.Models;
using ClashWinUI.Serialization;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace ClashWinUI.Services.Implementations.Config
{
    public class ConfigService : IConfigService
    {
        private const string SourceFileName = "source.yaml";
        private const string MixinFileName = "mixin.yaml";
        private const string RuntimeFileName = "runtime.yaml";
        private const string RulesOverrideFileName = "rules.overrides.json";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        private readonly IAppLogService _logService;
        private readonly string _profilesRoot;

        public event EventHandler? ConfigurationChanged;

        public ConfigService(IAppLogService logService)
        {
            _logService = logService;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _profilesRoot = Path.Combine(userProfile, "ClashWinUI", "Profiles");
            Directory.CreateDirectory(_profilesRoot);
        }

        public ProfileConfigWorkspace GetWorkspace(ProfileItem profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            string directoryPath = string.IsNullOrWhiteSpace(profile.WorkspaceDirectory)
                ? Path.Combine(_profilesRoot, profile.Id)
                : Path.GetFullPath(profile.WorkspaceDirectory.Trim());

            return new ProfileConfigWorkspace
            {
                DirectoryPath = directoryPath,
                SourcePath = Path.Combine(directoryPath, SourceFileName),
                MixinPath = Path.Combine(directoryPath, MixinFileName),
                RuntimePath = Path.Combine(directoryPath, RuntimeFileName),
                RulesOverridePath = Path.Combine(directoryPath, RulesOverrideFileName),
            };
        }

        public ProfileConfigWorkspace EnsureWorkspace(ProfileItem profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            ProfileConfigWorkspace workspace = GetWorkspace(profile);
            Directory.CreateDirectory(workspace.DirectoryPath);

            bool workspaceChanged = UpdateProfilePaths(profile, workspace);
            bool sourceChanged = EnsureSourceFile(profile, workspace);
            bool mixinCreated = EnsureMixinFile(workspace);

            if (!File.Exists(workspace.RuntimePath) || sourceChanged || mixinCreated)
            {
                BuildRuntimeInternal(workspace);
            }

            if (workspaceChanged || sourceChanged)
            {
                profile.UpdatedAt = DateTimeOffset.Now;
            }

            return workspace;
        }

        public MixinSettings LoadMixin(ProfileItem profile)
        {
            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);

            try
            {
                return ReadSettingsFromYamlFile(workspace.MixinPath);
            }
            catch (Exception ex)
            {
                _logService.Add($"Load mixin failed, fallback to source settings: {ex.Message}", LogLevel.Warning);
                return ReadSettingsFromYamlFile(workspace.SourcePath);
            }
        }

        public void SaveMixin(ProfileItem profile, MixinSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);
            WriteMixinSettings(workspace.MixinPath, settings);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        public string BuildRuntime(ProfileItem profile)
        {
            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);
            BuildRuntimeInternal(workspace);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            return workspace.RuntimePath;
        }

        public string GetRuntimePath(ProfileItem profile)
        {
            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);
            if (!File.Exists(workspace.RuntimePath))
            {
                BuildRuntimeInternal(workspace);
            }

            return workspace.RuntimePath;
        }

        public IReadOnlyList<RuntimeRuleItem> GetRuntimeRules(ProfileItem profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);
            YamlMappingNode merged = BuildMergedMapping(workspace);
            HashSet<string> disabledRuleIds = LoadDisabledRuleIds(workspace);
            return BuildRuntimeRuleItems(merged, disabledRuleIds);
        }

        public void SetRuleEnabled(ProfileItem profile, string stableId, bool isEnabled)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentException.ThrowIfNullOrWhiteSpace(stableId);

            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);
            HashSet<string> disabledRuleIds = LoadDisabledRuleIds(workspace);
            string normalizedId = stableId.Trim();

            if (isEnabled)
            {
                disabledRuleIds.Remove(normalizedId);
            }
            else
            {
                disabledRuleIds.Add(normalizedId);
            }

            SaveRulesOverrideState(workspace, disabledRuleIds);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool UpdateProfilePaths(ProfileItem profile, ProfileConfigWorkspace workspace)
        {
            bool changed = false;

            if (!PathsEqual(profile.WorkspaceDirectory, workspace.DirectoryPath))
            {
                profile.WorkspaceDirectory = workspace.DirectoryPath;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(profile.FilePath))
            {
                profile.FilePath = workspace.SourcePath;
                changed = true;
            }

            return changed;
        }

        private bool EnsureSourceFile(ProfileItem profile, ProfileConfigWorkspace workspace)
        {
            string currentPath = string.IsNullOrWhiteSpace(profile.FilePath)
                ? workspace.SourcePath
                : Path.GetFullPath(profile.FilePath.Trim());

            if (PathsEqual(currentPath, workspace.SourcePath))
            {
                profile.FilePath = workspace.SourcePath;
                return false;
            }

            if (File.Exists(currentPath))
            {
                File.Copy(currentPath, workspace.SourcePath, overwrite: true);
                TryDeleteLegacySource(currentPath, workspace.SourcePath);
                profile.FilePath = workspace.SourcePath;
                _logService.Add($"Profile source migrated to workspace: {workspace.SourcePath}");
                return true;
            }

            if (File.Exists(workspace.SourcePath))
            {
                profile.FilePath = workspace.SourcePath;
                return false;
            }

            File.WriteAllText(workspace.SourcePath, "proxies: []" + Environment.NewLine, Utf8NoBom);
            profile.FilePath = workspace.SourcePath;
            _logService.Add($"Profile source file missing, created placeholder source: {workspace.SourcePath}", LogLevel.Warning);
            return true;
        }

        private bool EnsureMixinFile(ProfileConfigWorkspace workspace)
        {
            if (File.Exists(workspace.MixinPath))
            {
                return false;
            }

            MixinSettings settings = ReadSettingsFromYamlFile(workspace.SourcePath);
            WriteMixinSettings(workspace.MixinPath, settings);
            return true;
        }

        private void BuildRuntimeInternal(ProfileConfigWorkspace workspace)
        {
            try
            {
                YamlMappingNode merged = BuildMergedMapping(workspace);
                ApplyRuleOverrides(workspace, merged);
                SaveYamlMapping(workspace.RuntimePath, merged, useBom: true);
            }
            catch (Exception ex)
            {
                _logService.Add($"Build runtime.yaml failed: {ex.Message}", LogLevel.Warning);
                throw;
            }
        }

        private YamlMappingNode BuildMergedMapping(ProfileConfigWorkspace workspace)
        {
            YamlMappingNode source = LoadYamlMapping(workspace.SourcePath);
            YamlMappingNode mixin = LoadYamlMapping(workspace.MixinPath);
            return MergeMappings(source, mixin);
        }

        private void ApplyRuleOverrides(ProfileConfigWorkspace workspace, YamlMappingNode merged)
        {
            HashSet<string> disabledRuleIds = LoadDisabledRuleIds(workspace);
            if (disabledRuleIds.Count == 0)
            {
                return;
            }

            if (!TryGetChild(merged, "rules", out YamlNode? rulesKey, out YamlNode? rulesNode)
                || rulesKey is null
                || rulesNode is not YamlSequenceNode rulesSequence)
            {
                return;
            }

            var filteredRules = new YamlSequenceNode();
            var occurrenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (YamlNode child in rulesSequence.Children)
            {
                string rawRuleText = ExtractRuleText(child);
                string normalizedRuleText = NormalizeRuleText(rawRuleText);
                int occurrence = NextRuleOccurrence(occurrenceCounts, normalizedRuleText);
                string stableId = BuildRuleStableId(normalizedRuleText, occurrence);
                if (disabledRuleIds.Contains(stableId))
                {
                    continue;
                }

                filteredRules.Add(CloneNode(child));
            }

            merged.Children[rulesKey] = filteredRules;
        }

        private IReadOnlyList<RuntimeRuleItem> BuildRuntimeRuleItems(YamlMappingNode merged, HashSet<string> disabledRuleIds)
        {
            if (!TryGetChild(merged, "rules", out _, out YamlNode? rulesNode) || rulesNode is not YamlSequenceNode rulesSequence)
            {
                return Array.Empty<RuntimeRuleItem>();
            }

            var items = new List<RuntimeRuleItem>();
            var occurrenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int index = 1;

            foreach (YamlNode child in rulesSequence.Children)
            {
                string rawRuleText = ExtractRuleText(child);
                if (string.IsNullOrWhiteSpace(rawRuleText))
                {
                    continue;
                }

                string normalizedRuleText = NormalizeRuleText(rawRuleText);
                int occurrence = NextRuleOccurrence(occurrenceCounts, normalizedRuleText);
                string stableId = BuildRuleStableId(normalizedRuleText, occurrence);
                items.Add(ParseRuntimeRuleItem(
                    stableId,
                    index++,
                    rawRuleText,
                    !disabledRuleIds.Contains(stableId)));
            }

            return items;
        }

        private HashSet<string> LoadDisabledRuleIds(ProfileConfigWorkspace workspace)
        {
            try
            {
                if (!File.Exists(workspace.RulesOverridePath))
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                string content = File.ReadAllText(workspace.RulesOverridePath);
                RulesOverrideState? state = JsonSerializer.Deserialize(content, ClashJsonContext.Default.RulesOverrideState);
                return state?.DisabledRuleIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logService.Add($"Load rule overrides failed: {ex.Message}", LogLevel.Warning);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveRulesOverrideState(ProfileConfigWorkspace workspace, HashSet<string> disabledRuleIds)
        {
            try
            {
                if (disabledRuleIds.Count == 0)
                {
                    if (File.Exists(workspace.RulesOverridePath))
                    {
                        File.Delete(workspace.RulesOverridePath);
                    }

                    return;
                }

                string? directory = Path.GetDirectoryName(workspace.RulesOverridePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var state = new RulesOverrideState
                {
                    DisabledRuleIds = disabledRuleIds
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };

                string content = JsonSerializer.Serialize(state, ClashJsonContext.Default.RulesOverrideState);
                File.WriteAllText(workspace.RulesOverridePath, content, Utf8NoBom);
            }
            catch (Exception ex)
            {
                _logService.Add($"Save rule overrides failed: {ex.Message}", LogLevel.Warning);
                throw;
            }
        }

        private static void TryDeleteLegacySource(string legacyPath, string newPath)
        {
            if (PathsEqual(legacyPath, newPath))
            {
                return;
            }

            try
            {
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }
            }
            catch
            {
                // Keep migration successful even if the old file cannot be deleted.
            }
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(left.Trim()),
                Path.GetFullPath(right.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }

        private static MixinSettings ReadSettingsFromYamlFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new MixinSettings();
            }

            YamlMappingNode root = LoadYamlMapping(path);
            return new MixinSettings
            {
                MixedPort = GetNullableInt(root, "mixed-port"),
                HttpPort = GetNullableInt(root, "port"),
                SocksPort = GetNullableInt(root, "socks-port"),
                RedirPort = GetNullableInt(root, "redir-port"),
                TProxyPort = GetNullableInt(root, "tproxy-port"),
                TunEnabled = GetTunEnabled(root),
                AllowLan = GetBool(root, "allow-lan"),
                Mode = NormalizeMode(GetString(root, "mode")),
                LogLevel = NormalizeLogLevel(GetString(root, "log-level")),
                Ipv6Enabled = GetBool(root, "ipv6"),
            };
        }

        private static void WriteMixinSettings(string path, MixinSettings settings)
        {
            var root = new YamlMappingNode();

            AddOptionalPort(root, "mixed-port", settings.MixedPort);
            AddOptionalPort(root, "port", settings.HttpPort);
            AddOptionalPort(root, "socks-port", settings.SocksPort);
            AddOptionalPort(root, "redir-port", settings.RedirPort);
            AddOptionalPort(root, "tproxy-port", settings.TProxyPort);
            AddBoolean(root, "allow-lan", settings.AllowLan);
            AddScalar(root, "mode", NormalizeMode(settings.Mode));
            AddScalar(root, "log-level", NormalizeLogLevel(settings.LogLevel));
            AddBoolean(root, "ipv6", settings.Ipv6Enabled);

            var tunMapping = new YamlMappingNode();
            AddBoolean(tunMapping, "enable", settings.TunEnabled);
            root.Add(new YamlScalarNode("tun"), tunMapping);

            SaveYamlMapping(path, root);
        }

        private static void AddOptionalPort(YamlMappingNode root, string key, int? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return;
            }

            AddScalar(root, key, value.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AddBoolean(YamlMappingNode root, string key, bool value)
        {
            AddScalar(root, key, value ? "true" : "false");
        }

        private static void AddScalar(YamlMappingNode root, string key, string value)
        {
            root.Add(new YamlScalarNode(key), new YamlScalarNode(value));
        }

        private static RuntimeRuleItem ParseRuntimeRuleItem(string stableId, int index, string rawRuleText, bool isEnabled)
        {
            string[] segments = rawRuleText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            string matcherType = segments.Length > 0
                ? segments[0].Trim().ToUpperInvariant()
                : "UNKNOWN";
            bool hasMatcherValue = !IsValueLessRuleType(matcherType);
            string matcherValue = hasMatcherValue && segments.Length > 1
                ? segments[1].Trim()
                : string.Empty;
            int actionIndex = hasMatcherValue ? 2 : 1;
            string actionTarget = segments.Length > actionIndex
                ? segments[actionIndex].Trim()
                : string.Empty;
            RuleActionKind actionKind = ClassifyRuleActionKind(actionTarget);

            return new RuntimeRuleItem
            {
                StableId = stableId,
                Index = index,
                MatcherType = matcherType,
                MatcherTypeDisplay = FormatMatcherTypeDisplay(matcherType),
                MatcherValue = matcherValue,
                MatcherValueDisplay = matcherValue,
                RawRuleText = rawRuleText,
                ActionKind = actionKind,
                ActionKindDisplay = FormatActionKindDisplay(actionKind, actionTarget),
                ActionTargetRaw = actionTarget,
                ActionTargetDisplay = actionKind == RuleActionKind.Proxy ? actionTarget : string.Empty,
                IsEnabled = isEnabled,
            };
        }

        private static YamlMappingNode LoadYamlMapping(string path)
        {
            string content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                return new YamlMappingNode();
            }

            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                return new YamlMappingNode();
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                throw new InvalidDataException($"YAML root must be a mapping: {path}");
            }

            return mapping;
        }

        private static void SaveYamlMapping(string path, YamlMappingNode root, bool useBom = false)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new YamlStream(new YamlDocument(root));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            stream.Save(writer, false);
            File.WriteAllText(path, writer.ToString(), useBom ? Utf8Bom : Utf8NoBom);
        }

        private static YamlMappingNode MergeMappings(YamlMappingNode source, YamlMappingNode mixin)
        {
            var merged = (YamlMappingNode)CloneNode(source);

            foreach (KeyValuePair<YamlNode, YamlNode> pair in mixin.Children)
            {
                string? key = GetScalarValue(pair.Key);
                if (string.IsNullOrWhiteSpace(key))
                {
                    merged.Add(CloneNode(pair.Key), CloneNode(pair.Value));
                    continue;
                }

                if (TryGetChild(merged, key, out YamlNode? existingKey, out YamlNode? existingValue))
                {
                    merged.Children[existingKey!] = MergeNodes(existingValue!, pair.Value);
                }
                else
                {
                    merged.Add(new YamlScalarNode(key), CloneNode(pair.Value));
                }
            }

            return merged;
        }

        private static YamlNode MergeNodes(YamlNode source, YamlNode mixin)
        {
            if (source is YamlMappingNode sourceMapping && mixin is YamlMappingNode mixinMapping)
            {
                return MergeMappings(sourceMapping, mixinMapping);
            }

            if (mixin is YamlSequenceNode)
            {
                return CloneNode(mixin);
            }

            return CloneNode(mixin);
        }

        private static YamlNode CloneNode(YamlNode node)
        {
            return node switch
            {
                YamlScalarNode scalar => new YamlScalarNode(scalar.Value),
                YamlSequenceNode sequence => new YamlSequenceNode(sequence.Children.Select(CloneNode)),
                YamlMappingNode mapping => new YamlMappingNode(
                    mapping.Children.Select(pair => new KeyValuePair<YamlNode, YamlNode>(CloneNode(pair.Key), CloneNode(pair.Value)))),
                _ => throw new NotSupportedException($"Unsupported YAML node type: {node.GetType().Name}"),
            };
        }

        private static bool TryGetChild(YamlMappingNode mapping, string key, out YamlNode? existingKey, out YamlNode? existingValue)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
            {
                if (string.Equals(GetScalarValue(pair.Key), key, StringComparison.OrdinalIgnoreCase))
                {
                    existingKey = pair.Key;
                    existingValue = pair.Value;
                    return true;
                }
            }

            existingKey = null;
            existingValue = null;
            return false;
        }

        private static int? GetNullableInt(YamlMappingNode mapping, string key)
        {
            string? value = GetString(mapping, key);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
                ? result
                : null;
        }

        private static bool GetBool(YamlMappingNode mapping, string key)
        {
            string? value = GetString(mapping, key);
            return bool.TryParse(value, out bool result) && result;
        }

        private static string? GetString(YamlMappingNode mapping, string key)
        {
            return TryGetChild(mapping, key, out _, out YamlNode? valueNode)
                ? GetScalarValue(valueNode)
                : null;
        }

        private static bool GetTunEnabled(YamlMappingNode mapping)
        {
            if (!TryGetChild(mapping, "tun", out _, out YamlNode? tunNode))
            {
                return false;
            }

            return tunNode switch
            {
                YamlMappingNode tunMapping => GetBool(tunMapping, "enable"),
                YamlScalarNode scalar when bool.TryParse(scalar.Value, out bool value) => value,
                _ => false,
            };
        }

        private static string? GetScalarValue(YamlNode? node)
        {
            return (node as YamlScalarNode)?.Value;
        }

        private static string ExtractRuleText(YamlNode node)
        {
            return node switch
            {
                YamlScalarNode scalar => scalar.Value?.Trim() ?? string.Empty,
                _ => node.ToString().Trim(),
            };
        }

        private static string NormalizeRuleText(string rawRuleText)
        {
            if (string.IsNullOrWhiteSpace(rawRuleText))
            {
                return string.Empty;
            }

            string[] segments = rawRuleText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim().ToUpperInvariant())
                .ToArray();

            return string.Join(",", segments);
        }

        private static int NextRuleOccurrence(Dictionary<string, int> occurrenceCounts, string normalizedRuleText)
        {
            if (occurrenceCounts.TryGetValue(normalizedRuleText, out int current))
            {
                current++;
            }
            else
            {
                current = 1;
            }

            occurrenceCounts[normalizedRuleText] = current;
            return current;
        }

        private static string BuildRuleStableId(string normalizedRuleText, int occurrence)
        {
            string payload = $"{normalizedRuleText}|{occurrence.ToString(CultureInfo.InvariantCulture)}";
            byte[] hash = SHA256.HashData(Utf8NoBom.GetBytes(payload));
            return Convert.ToHexString(hash);
        }

        private static bool IsValueLessRuleType(string matcherType)
        {
            return string.Equals(matcherType, "MATCH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(matcherType, "FINAL", StringComparison.OrdinalIgnoreCase);
        }

        private static RuleActionKind ClassifyRuleActionKind(string actionTarget)
        {
            if (string.IsNullOrWhiteSpace(actionTarget))
            {
                return RuleActionKind.Other;
            }

            string normalized = actionTarget.Trim().ToUpperInvariant();
            if (normalized == "DIRECT")
            {
                return RuleActionKind.Direct;
            }

            if (normalized == "PASS")
            {
                return RuleActionKind.Pass;
            }

            if (normalized.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase))
            {
                return RuleActionKind.Reject;
            }

            return RuleActionKind.Proxy;
        }

        private static string FormatMatcherTypeDisplay(string matcherType)
        {
            return matcherType switch
            {
                "DOMAIN" => "Domain",
                "DOMAIN-SUFFIX" => "DomainSuffix",
                "DOMAIN-KEYWORD" => "DomainKeyword",
                "IP-CIDR" => "IPCIDR",
                "IP-CIDR6" => "IPCIDR6",
                "GEOIP" => "GeoIP",
                "GEOSITE" => "GeoSite",
                "RULE-SET" => "RuleSet",
                "PROCESS-NAME" => "ProcessName",
                "MATCH" => "Match",
                "FINAL" => "Final",
                _ => ToPascalToken(matcherType),
            };
        }

        private static string FormatActionKindDisplay(RuleActionKind actionKind, string actionTarget)
        {
            return actionKind switch
            {
                RuleActionKind.Direct => "DIRECT",
                RuleActionKind.Proxy => "PROXY",
                RuleActionKind.Reject => string.IsNullOrWhiteSpace(actionTarget)
                    ? "REJECT"
                    : actionTarget.Trim().ToUpperInvariant(),
                RuleActionKind.Pass => "PASS",
                _ => string.IsNullOrWhiteSpace(actionTarget)
                    ? "OTHER"
                    : actionTarget.Trim().ToUpperInvariant(),
            };
        }

        private static string ToPascalToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Unknown";
            }

            string[] segments = raw
                .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return string.Concat(segments.Select(segment =>
            {
                if (segment.Length == 0)
                {
                    return string.Empty;
                }

                if (segment.Length == 1)
                {
                    return segment.ToUpperInvariant();
                }

                return char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant();
            }));
        }

        private static string NormalizeMode(string? raw)
        {
            return raw?.Trim().ToLowerInvariant() switch
            {
                "global" => "global",
                "direct" => "direct",
                _ => "rule",
            };
        }

        private static string NormalizeLogLevel(string? raw)
        {
            return raw?.Trim().ToLowerInvariant() switch
            {
                "debug" => "debug",
                "warning" => "warning",
                "error" => "error",
                _ => "info",
            };
        }
    }
}
