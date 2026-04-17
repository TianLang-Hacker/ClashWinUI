using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.ApplicationModel;

namespace ClashWinUI.Helpers
{
    internal static class AppPackageInfoHelper
    {
        private static readonly AppPackageIdentityInfo CachedIdentity = ResolveIdentity();

        public static AppPackageIdentityInfo Current => CachedIdentity;

        public static bool IsPackaged()
        {
            try
            {
                _ = Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string? TryGetPackageFamilyName()
        {
            try
            {
                return Package.Current.Id.FamilyName?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static AppPackageIdentityInfo ResolveIdentity()
        {
            if (TryGetFromPackage(out AppPackageIdentityInfo? packagedIdentity))
            {
                return packagedIdentity!;
            }

            string[] manifestCandidates =
            [
                Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml"),
                Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"),
                Path.Combine(Directory.GetCurrentDirectory(), "Package.appxmanifest"),
            ];

            foreach (string path in manifestCandidates)
            {
                if (TryGetFromManifest(path, out AppPackageIdentityInfo? manifestIdentity))
                {
                    return manifestIdentity!;
                }
            }

            return new AppPackageIdentityInfo(
                name: "ClashWinUI",
                publisher: "CN=TianLang Hacker",
                version: new Version(1, 0, 0, 0),
                architecture: GetFallbackArchitecture());
        }

        private static bool TryGetFromPackage(out AppPackageIdentityInfo? identity)
        {
            try
            {
                Package package = Package.Current;
                PackageId id = package.Id;
                PackageVersion version = id.Version;

                identity = new AppPackageIdentityInfo(
                    name: id.Name,
                    publisher: id.Publisher,
                    version: new Version(version.Major, version.Minor, version.Build, version.Revision),
                    architecture: MapArchitecture(id.Architecture.ToString()));
                return true;
            }
            catch
            {
                identity = null;
                return false;
            }
        }

        private static bool TryGetFromManifest(string path, out AppPackageIdentityInfo? identity)
        {
            identity = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                XDocument document = XDocument.Load(path);
                XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
                XElement? identityElement = document.Root?.Element(ns + "Identity");
                if (identityElement is null)
                {
                    return false;
                }

                string name = identityElement.Attribute("Name")?.Value?.Trim() ?? "ClashWinUI";
                string publisher = identityElement.Attribute("Publisher")?.Value?.Trim() ?? "CN=TianLang Hacker";
                string rawVersion = identityElement.Attribute("Version")?.Value?.Trim() ?? "1.0.0.0";
                if (!Version.TryParse(rawVersion, out Version? version))
                {
                    version = new Version(1, 0, 0, 0);
                }

                identity = new AppPackageIdentityInfo(
                    name: name,
                    publisher: publisher,
                    version: version,
                    architecture: GetFallbackArchitecture());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetFallbackArchitecture()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64",
            };
        }

        private static string MapArchitecture(string raw)
        {
            return raw.ToLowerInvariant() switch
            {
                "x86" => "x86",
                "arm64" => "arm64",
                _ => "x64",
            };
        }
    }

    internal sealed class AppPackageIdentityInfo
    {
        public AppPackageIdentityInfo(string name, string publisher, Version version, string architecture)
        {
            Name = name;
            Publisher = publisher;
            Version = version;
            Architecture = architecture;
        }

        public string Name { get; }

        public string Publisher { get; }

        public Version Version { get; }

        public string Architecture { get; }
    }
}
