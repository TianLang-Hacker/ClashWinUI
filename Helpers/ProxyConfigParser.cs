using ClashWinUI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;

namespace ClashWinUI.Helpers
{
    public static class ProxyConfigParser
    {
        private static readonly Regex ProxiesHeaderRegex = new(@"^(?<indent>\s*)proxies\s*:\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProxyNameRegex = new(@"^\s*-\s*name\s*:\s*(?<name>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProxyTypeRegex = new(@"^\s*type\s*:\s*(?<type>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<ProxyNode> ParseFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Array.Empty<ProxyNode>();
            }

            byte[] rawContent = File.ReadAllBytes(filePath);
            SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(rawContent);

            byte[] parseContent = normalization.Status switch
            {
                SubscriptionContentNormalizationStatus.DecodedFromBase64 => normalization.Content,
                SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml => normalization.Content,
                _ => rawContent,
            };

            string content = Encoding.UTF8.GetString(parseContent);
            return ParseFromContent(content);
        }

        public static IReadOnlyList<ProxyNode> ParseFromContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return Array.Empty<ProxyNode>();
            }

            IReadOnlyList<ProxyNode> yamlNodes = ParseYamlContent(content);
            if (yamlNodes.Count > 0)
            {
                return yamlNodes;
            }

            return ShareLinkSubscriptionConverter.ParseProxyNodes(Encoding.UTF8.GetBytes(content));
        }

        private static IReadOnlyList<ProxyNode> ParseYamlContent(string content)
        {
            var nodes = new List<ProxyNode>();
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            bool inProxiesSection = false;
            int proxiesIndent = -1;
            ProxyNode? currentNode = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine;
                Match headerMatch = ProxiesHeaderRegex.Match(line);
                if (!inProxiesSection && headerMatch.Success)
                {
                    inProxiesSection = true;
                    proxiesIndent = headerMatch.Groups["indent"].Value.Length;
                    continue;
                }

                if (!inProxiesSection)
                {
                    continue;
                }

                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                int currentIndent = line.Length - line.TrimStart().Length;
                if (currentIndent <= proxiesIndent && !line.TrimStart().StartsWith("-", StringComparison.Ordinal))
                {
                    break;
                }

                Match nameMatch = ProxyNameRegex.Match(line);
                if (nameMatch.Success)
                {
                    string name = NormalizeYamlScalar(nameMatch.Groups["name"].Value);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        currentNode = new ProxyNode
                        {
                            Name = name,
                            Type = "unknown",
                        };
                        nodes.Add(currentNode);
                    }

                    continue;
                }

                if (currentNode is null)
                {
                    continue;
                }

                Match typeMatch = ProxyTypeRegex.Match(line);
                if (typeMatch.Success)
                {
                    currentNode.Type = NormalizeYamlScalar(typeMatch.Groups["type"].Value);
                }
            }

            return nodes;
        }

        private static string NormalizeYamlScalar(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length >= 2)
            {
                bool isDoubleQuoted = trimmed[0] == '"' && trimmed[^1] == '"';
                bool isSingleQuoted = trimmed[0] == '\'' && trimmed[^1] == '\'';
                if (isDoubleQuoted || isSingleQuoted)
                {
                    trimmed = trimmed[1..^1];
                }
            }

            return trimmed;
        }
    }
}
