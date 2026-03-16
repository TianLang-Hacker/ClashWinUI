using ClashWinUI.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClashWinUI.Helpers
{
    public static class ShareLinkSubscriptionConverter
    {
        private static readonly Regex VmessLinkRegex = new(
            @"vmess://(?<payload>[A-Za-z0-9\-_=/+]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool CanConvertToMihomoYaml(byte[] rawContent)
        {
            return TryParseVmessProxies(rawContent, out _, out _);
        }

        public static bool TryConvertToMihomoYaml(
            byte[] rawContent,
            out byte[] yamlContent,
            out int proxyCount)
        {
            yamlContent = [];
            proxyCount = 0;

            if (!TryParseVmessProxies(rawContent, out List<VmessProxySpec> proxies, out _))
            {
                return false;
            }

            string yaml = BuildMihomoYaml(proxies);
            yamlContent = Encoding.UTF8.GetBytes(yaml);
            proxyCount = proxies.Count;
            return true;
        }

        public static IReadOnlyList<ProxyNode> ParseProxyNodes(byte[] rawContent)
        {
            if (!TryParseVmessProxies(rawContent, out List<VmessProxySpec> proxies, out _))
            {
                return Array.Empty<ProxyNode>();
            }

            var nodes = new List<ProxyNode>(proxies.Count);
            foreach (VmessProxySpec proxy in proxies)
            {
                nodes.Add(new ProxyNode
                {
                    Name = proxy.Name,
                    Type = "vmess",
                });
            }

            return nodes;
        }

        private static bool TryParseVmessProxies(
            byte[] rawContent,
            out List<VmessProxySpec> proxies,
            out string normalizedText)
        {
            proxies = new List<VmessProxySpec>();
            normalizedText = string.Empty;

            if (rawContent is null || rawContent.Length == 0)
            {
                return false;
            }

            string text = Encoding.UTF8.GetString(rawContent);
            normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return false;
            }

            MatchCollection matches = VmessLinkRegex.Matches(normalizedText);
            if (matches.Count == 0)
            {
                return false;
            }

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 1;
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                string payload = match.Groups["payload"].Value.Trim();
                if (!TryDecodeBase64Text(payload, out string? jsonText) || string.IsNullOrWhiteSpace(jsonText))
                {
                    continue;
                }

                if (!TryParseVmessJson(jsonText, index, usedNames, out VmessProxySpec? proxySpec)
                    || proxySpec is null)
                {
                    continue;
                }

                proxies.Add(proxySpec);
                index++;
            }

            return proxies.Count > 0;
        }

        private static bool TryParseVmessJson(
            string jsonText,
            int index,
            HashSet<string> usedNames,
            out VmessProxySpec? proxySpec)
        {
            proxySpec = null;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(jsonText);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                string server = GetString(root, "add");
                string uuid = GetString(root, "id");
                int port = ParsePort(GetString(root, "port"));

                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uuid) || port <= 0)
                {
                    return false;
                }

                string baseName = GetString(root, "ps");
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = $"vmess-{index}";
                }

                string uniqueName = MakeUniqueName(baseName, usedNames);

                string network = GetString(root, "net");
                if (string.IsNullOrWhiteSpace(network))
                {
                    network = "tcp";
                }

                string host = GetString(root, "host");
                string path = GetString(root, "path");
                string tlsFlag = GetString(root, "tls");
                string sni = GetString(root, "sni");
                string cipher = GetString(root, "scy");
                string aidRaw = GetString(root, "aid");

                if (string.IsNullOrWhiteSpace(cipher))
                {
                    cipher = "auto";
                }

                int alterId = 0;
                _ = int.TryParse(aidRaw, out alterId);
                bool useTls = string.Equals(tlsFlag, "tls", StringComparison.OrdinalIgnoreCase);
                string serverName = !string.IsNullOrWhiteSpace(sni) ? sni : host;

                proxySpec = new VmessProxySpec
                {
                    Name = uniqueName,
                    Server = server,
                    Port = port,
                    Uuid = uuid,
                    AlterId = Math.Max(0, alterId),
                    Cipher = cipher,
                    Network = network.ToLowerInvariant(),
                    Host = host,
                    Path = path,
                    UseTls = useTls,
                    ServerName = serverName,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildMihomoYaml(List<VmessProxySpec> proxies)
        {
            var builder = new StringBuilder();
            builder.AppendLine("mixed-port: 7890");
            builder.AppendLine("allow-lan: false");
            builder.AppendLine("mode: Rule");
            builder.AppendLine("log-level: info");
            builder.AppendLine("external-controller: 127.0.0.1:9090");
            builder.AppendLine();
            builder.AppendLine("proxies:");

            foreach (VmessProxySpec proxy in proxies)
            {
                builder.AppendLine($"  - name: {QuoteYaml(proxy.Name)}");
                builder.AppendLine("    type: vmess");
                builder.AppendLine($"    server: {QuoteYaml(proxy.Server)}");
                builder.AppendLine($"    port: {proxy.Port}");
                builder.AppendLine($"    uuid: {QuoteYaml(proxy.Uuid)}");
                builder.AppendLine($"    alterId: {proxy.AlterId}");
                builder.AppendLine($"    cipher: {QuoteYaml(proxy.Cipher)}");
                builder.AppendLine("    udp: true");

                if (proxy.UseTls)
                {
                    builder.AppendLine("    tls: true");
                    if (!string.IsNullOrWhiteSpace(proxy.ServerName))
                    {
                        builder.AppendLine($"    servername: {QuoteYaml(proxy.ServerName)}");
                    }
                }

                if (!string.Equals(proxy.Network, "tcp", StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine($"    network: {QuoteYaml(proxy.Network)}");
                }

                if (string.Equals(proxy.Network, "ws", StringComparison.OrdinalIgnoreCase))
                {
                    string wsPath = string.IsNullOrWhiteSpace(proxy.Path) ? "/" : proxy.Path;
                    builder.AppendLine("    ws-opts:");
                    builder.AppendLine($"      path: {QuoteYaml(wsPath)}");
                    if (!string.IsNullOrWhiteSpace(proxy.Host))
                    {
                        builder.AppendLine("      headers:");
                        builder.AppendLine($"        Host: {QuoteYaml(proxy.Host)}");
                    }
                }
                else if (string.Equals(proxy.Network, "grpc", StringComparison.OrdinalIgnoreCase))
                {
                    string serviceName = string.IsNullOrWhiteSpace(proxy.Path)
                        ? "grpc"
                        : proxy.Path.Trim().TrimStart('/');
                    if (string.IsNullOrWhiteSpace(serviceName))
                    {
                        serviceName = "grpc";
                    }

                    builder.AppendLine("    grpc-opts:");
                    builder.AppendLine($"      grpc-service-name: {QuoteYaml(serviceName)}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("proxy-groups:");
            builder.AppendLine("  - name: 'Proxy'");
            builder.AppendLine("    type: select");
            builder.AppendLine("    proxies:");
            foreach (VmessProxySpec proxy in proxies)
            {
                builder.AppendLine($"      - {QuoteYaml(proxy.Name)}");
            }
            builder.AppendLine("      - DIRECT");
            builder.AppendLine();
            builder.AppendLine("rules:");
            builder.AppendLine("  - MATCH,Proxy");
            return builder.ToString();
        }

        private static string GetString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement property))
            {
                return string.Empty;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString() ?? string.Empty,
                JsonValueKind.Number => property.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
        }

        private static int ParsePort(string raw)
        {
            if (int.TryParse(raw, out int port) && port > 0 && port <= 65535)
            {
                return port;
            }

            return 0;
        }

        private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
        {
            string candidate = string.IsNullOrWhiteSpace(baseName) ? "vmess" : baseName.Trim();
            if (usedNames.Add(candidate))
            {
                return candidate;
            }

            int suffix = 2;
            while (true)
            {
                string next = $"{candidate}-{suffix}";
                if (usedNames.Add(next))
                {
                    return next;
                }

                suffix++;
            }
        }

        private static string QuoteYaml(string value)
        {
            string escaped = (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
            return $"'{escaped}'";
        }

        private static bool TryDecodeBase64Text(string input, out string? decodedText)
        {
            decodedText = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            string compact = input.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Trim();
            compact = compact.Replace('-', '+').Replace('_', '/');
            int remainder = compact.Length % 4;
            if (remainder > 0)
            {
                compact = compact.PadRight(compact.Length + (4 - remainder), '=');
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(compact);
                decodedText = Encoding.UTF8.GetString(bytes);
                return !string.IsNullOrWhiteSpace(decodedText);
            }
            catch
            {
                return false;
            }
        }

        private sealed class VmessProxySpec
        {
            public required string Name { get; init; }
            public required string Server { get; init; }
            public required int Port { get; init; }
            public required string Uuid { get; init; }
            public required int AlterId { get; init; }
            public required string Cipher { get; init; }
            public required string Network { get; init; }
            public required string Host { get; init; }
            public required string Path { get; init; }
            public required bool UseTls { get; init; }
            public required string ServerName { get; init; }
        }
    }
}
