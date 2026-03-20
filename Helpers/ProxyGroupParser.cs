using ClashWinUI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace ClashWinUI.Helpers
{
    public static class ProxyGroupParser
    {
        public static IReadOnlyList<ProxyGroup> ParseFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Array.Empty<ProxyGroup>();
            }

            YamlMappingNode root = LoadYamlMapping(filePath);
            Dictionary<string, ProxyNode> nodeMap = ParseNodes(root);
            List<ProxyGroup> groups = ParseGroups(root, nodeMap);

            if (groups.Count == 0 && nodeMap.Count > 0)
            {
                var fallbackGroup = new ProxyGroup
                {
                    Name = "Proxy",
                    Type = "select",
                };

                foreach (ProxyNode node in nodeMap.Values)
                {
                    fallbackGroup.Members.Add(new ProxyGroupMember
                    {
                        GroupName = fallbackGroup.Name,
                        Node = node,
                    });
                }

                fallbackGroup.SetCurrentProxy(fallbackGroup.Members.FirstOrDefault()?.Node.Name);
                groups.Add(fallbackGroup);
            }

            return groups;
        }

        private static Dictionary<string, ProxyNode> ParseNodes(YamlMappingNode root)
        {
            var nodes = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);

            if (!TryGetChild(root, "proxies", out YamlNode? proxiesNode) || proxiesNode is not YamlSequenceNode proxiesSequence)
            {
                return nodes;
            }

            foreach (YamlMappingNode proxyMapping in proxiesSequence.Children.OfType<YamlMappingNode>())
            {
                string name = GetString(proxyMapping, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                bool supportsUdp = GetBool(proxyMapping, "udp");
                string network = GetString(proxyMapping, "network");

                nodes[name] = new ProxyNode
                {
                    Name = name,
                    Type = NormalizeProxyType(GetString(proxyMapping, "type")),
                    SupportsUdp = supportsUdp,
                    TransportText = GetTransportText(network),
                    Network = network,
                };
            }

            return nodes;
        }

        private static List<ProxyGroup> ParseGroups(YamlMappingNode root, Dictionary<string, ProxyNode> nodeMap)
        {
            var groups = new List<ProxyGroup>();

            if (!TryGetChild(root, "proxy-groups", out YamlNode? proxyGroupsNode)
                || proxyGroupsNode is not YamlSequenceNode groupsSequence)
            {
                return groups;
            }

            foreach (YamlMappingNode groupMapping in groupsSequence.Children.OfType<YamlMappingNode>())
            {
                string name = GetString(groupMapping, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var group = new ProxyGroup
                {
                    Name = name,
                    Type = NormalizeGroupType(GetString(groupMapping, "type")),
                };

                if (TryGetChild(groupMapping, "proxies", out YamlNode? membersNode)
                    && membersNode is YamlSequenceNode membersSequence)
                {
                    foreach (YamlScalarNode memberScalar in membersSequence.Children.OfType<YamlScalarNode>())
                    {
                        string memberName = memberScalar.Value?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(memberName))
                        {
                            continue;
                        }

                        if (!nodeMap.TryGetValue(memberName, out ProxyNode? node))
                        {
                            node = CreatePlaceholderNode(memberName);
                            nodeMap[memberName] = node;
                        }

                        group.Members.Add(new ProxyGroupMember
                        {
                            GroupName = group.Name,
                            Node = node,
                        });
                    }
                }

                group.SetCurrentProxy(group.Members.FirstOrDefault()?.Node.Name);
                groups.Add(group);
            }

            return groups;
        }

        private static ProxyNode CreatePlaceholderNode(string name)
        {
            return new ProxyNode
            {
                Name = name,
                Type = InferPlaceholderType(name),
                SupportsUdp = false,
                TransportText = string.Empty,
            };
        }

        private static string InferPlaceholderType(string name)
        {
            return name.Trim().ToUpperInvariant() switch
            {
                "DIRECT" => "direct",
                "REJECT" => "reject",
                "REJECT-DROP" => "reject",
                "PASS" => "pass",
                _ => "group",
            };
        }

        private static YamlMappingNode LoadYamlMapping(string path)
        {
            using var reader = new StringReader(File.ReadAllText(path));
            var stream = new YamlStream();
            stream.Load(reader);

            return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode mapping
                ? mapping
                : new YamlMappingNode();
        }

        private static bool TryGetChild(YamlMappingNode mapping, string key, out YamlNode? value)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
            {
                if (pair.Key is YamlScalarNode scalar
                    && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static string GetString(YamlMappingNode mapping, string key)
        {
            if (!TryGetChild(mapping, key, out YamlNode? value))
            {
                return string.Empty;
            }

            return (value as YamlScalarNode)?.Value?.Trim() ?? string.Empty;
        }

        private static bool GetBool(YamlMappingNode mapping, string key)
        {
            string value = GetString(mapping, key);
            return bool.TryParse(value, out bool result) && result;
        }

        private static string GetTransportText(string network)
        {
            return string.IsNullOrWhiteSpace(network)
                ? string.Empty
                : network.Trim();
        }

        private static string NormalizeProxyType(string? raw)
        {
            return string.IsNullOrWhiteSpace(raw)
                ? "unknown"
                : raw.Trim().ToLower(CultureInfo.InvariantCulture);
        }

        private static string NormalizeGroupType(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "selector";
            }

            string normalized = raw.Trim()
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLower(CultureInfo.InvariantCulture);

            return normalized switch
            {
                "select" => "selector",
                "selector" => "selector",
                "urltest" => "urltest",
                "fallback" => "fallback",
                "loadbalance" => "loadbalance",
                "direct" => "direct",
                "reject" => "reject",
                "compatible" => "compatible",
                _ => normalized,
            };
        }
    }
}
