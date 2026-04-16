using Microsoft.Win32;
using System;

namespace ClashWinUI.Helpers
{
    public static class WindowsFirewallHelper
    {
        private const string FirewallPolicyKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

        private static readonly string[] ProfileKeys =
        [
            "DomainProfile",
            "PublicProfile",
            "StandardProfile",
            "PrivateProfile",
        ];

        public static bool IsAnyProfileEnabled()
        {
            try
            {
                using RegistryKey root = Registry.LocalMachine.OpenSubKey(FirewallPolicyKey, writable: false)
                    ?? throw new InvalidOperationException("Firewall policy registry key not found.");

                foreach (string profileKey in ProfileKeys)
                {
                    using RegistryKey? profile = root.OpenSubKey(profileKey, writable: false);
                    object? value = profile?.GetValue("EnableFirewall");
                    if (value is int intValue && intValue != 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return true;
            }

            return false;
        }
    }
}
