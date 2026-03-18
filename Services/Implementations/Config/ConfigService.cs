
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace ClashWinUI.Services.Implementations.Config
{
    public class ConfigService : IConfigService
    {
        private const string SourceFileName = "source.yaml";
        private const string MixinFileName = "mixin.yaml";
        private const string RuntimeFileName = "runtime.yaml";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly IAppLogService _logService;
        private readonly string _profilesRoot;

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
        }

        public string BuildRuntime(ProfileItem profile)
        {
            ProfileConfigWorkspace workspace = EnsureWorkspace(profile);
            BuildRuntimeInternal(workspace);
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
                YamlMappingNode source = LoadYamlMapping(workspace.SourcePath);
                YamlMappingNode mixin = LoadYamlMapping(workspace.MixinPath);
                YamlMappingNode merged = MergeMappings(source, mixin);
                SaveYamlMapping(workspace.RuntimePath, merged);
            }
            catch (Exception ex)
            {
                _logService.Add($"Build runtime.yaml failed: {ex.Message}", LogLevel.Warning);
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

        private static void SaveYamlMapping(string path, YamlMappingNode root)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new YamlStream(new YamlDocument(root));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            stream.Save(writer, false);
            File.WriteAllText(path, writer.ToString(), Utf8NoBom);
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
