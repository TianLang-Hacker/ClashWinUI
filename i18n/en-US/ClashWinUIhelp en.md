# ClashWinUI Project Structure Overview

## Root Directory and Main Files

| File/Folder | Description |
| - | - |
| `App.xaml` | Application entry XAML that defines global resources, styles, and theme resources. |
| `App.xaml.cs` | Application startup code that builds the Host/DI container, runs the startup pipeline, handles unhandled exceptions, initializes the tray icon, starts update checks, and boots Mihomo/GeoData. |
| `Package.appxmanifest` | WinUI 3 package manifest that defines package identity, capabilities, icons, and launch behavior. |
| `Properties/` | Project property folder containing `launchSettings.json` and publish profiles. |
| `Assets/` | Packaged icons, splash screen assets, and Store images. |
| `Build/` | Build-related scripts, including Mihomo kernel and GeoData download helpers. |
| `i18n/` | Localization resources, localized READMEs, and localized help documents. |
| `Kernels/` | Placeholder kernel folder kept in the repo; currently only contains `.gitkeep`. |
| `bin/`, `obj/` | Local build output folders. |
| `AppPackages/`, `BundleArtifacts/` | Packaging outputs and packaging helper artifacts. |

## Common/

| File | Description |
| - | - |
| `AppConstants.cs` | Global constants such as controller ports, route keys, and app-level defaults. |

## Converters/

`Converters/` contains page binding converters that translate booleans, strings, and delay levels into UI-facing types such as `Brush` and `Visibility`.

| File | Description |
| - | - |
| `BooleanToCardBackgroundBrushConverter.cs` | Switches card backgrounds from a boolean state. |
| `BooleanToCardBorderBrushConverter.cs` | Switches card borders from a boolean state. |
| `BooleanToVisibilityConverter.cs` | Converts booleans to visibility. |
| `StringToVisibilityConverter.cs` | Converts empty/non-empty strings to visibility. |
| `ProxyDelayLevelToBrushConverter.cs` | Maps proxy delay levels to colors. |

## Helpers/

`Helpers/` contains reusable utility and adapter classes shared across pages and services.

| File | Description |
| - | - |
| `AppPackageInfoHelper.cs` | Resolves package identity, version, publisher, and architecture for settings and update flows. |
| `GeoDataStatusTextHelper.cs` | Builds user-facing GeoData status text. |
| `LiveChartsBootstrapper.cs` | Lazy initialization entry point for HomePage LiveCharts charts. |
| `LocalizedStrings.cs` | Localized string access wrapper. |
| `PageMemoryTrimHelper.cs` | Triggers GC and working-set trimming after heavy pages unload or the shell freezes. |
| `ProfileCompatibilityChecker.cs` | Checks whether a profile/config is compatible with the current runtime environment. |
| `ProxyConfigParser.cs` | Parses proxy node configuration. |
| `ProxyGroupParser.cs` | Parses proxy groups and members. |
| `ShareLinkSubscriptionConverter.cs` | Converts share-link subscriptions into Mihomo YAML. |
| `SubscriptionContentNormalizer.cs` | Normalizes subscription encoding and content format. |

## Models/

`Models/` defines application state objects, page view models, and runtime data structures.

| File | Description |
| - | - |
| `CloseBehavior.cs` | Application close-behavior definitions. |
| `ConnectionEntry.cs`, `ConnectionsColumnLayout.cs` | Connection page row model and column layout state. |
| `GeoDataAssetStatus.cs`, `GeoDataFailureKind.cs`, `GeoDataOperationKind.cs`, `GeoDataOperationResult.cs` | GeoData download and verification result models. |
| `HomeChartSample.cs`, `HomeChartState.cs`, `HomeOverviewState.cs` | Home chart samples, chart history cache, and dashboard snapshot models. |
| `LogEntry.cs` | Single log item model for the logs page. |
| `MihomoFailureDiagnostic.cs`, `MihomoFailureKind.cs` | Mihomo runtime failure diagnostics. |
| `MixinSettings.cs`, `PortSettingsDraft.cs`, `ProfileConfigWorkspace.cs`, `ProfileItem.cs` | Profile workspace, mixin settings, and subscription metadata models. |
| `ProxyGroup.cs`, `ProxyGroupLoadResult.cs`, `ProxyGroupMember.cs`, `ProxyNode.cs` | Proxy groups, members, nodes, and load result models. |
| `PublicNetworkInfo.cs` | Public network information model used by the Home dashboard. |
| `RuntimeRuleItem.cs` | Runtime rule model used by the rules page. |
| `UpdateState.cs`, `UpdateStatus.cs` | State models for update check, download, and installation flows. |

## Serialization/

| File | Description |
| - | - |
| `ClashJsonContext.cs` | `System.Text.Json` source-generated context used to centralize JSON model registration and improve serialization performance. |

### Interfaces/

| Interface | Description |
| - | - |
| `IAppLogService.cs` | Application log collection and query interface. |
| `IAppSettingsService.cs` | App settings read/write interface. |
| `IConfigService.cs` | Interface for subscription config, mixin, runtime, and rule-toggle management. |
| `IGeoDataService.cs` | GeoData prepare/refresh/status interface. |
| `IHomeChartStateService.cs` | Home dashboard chart history cache interface. |
| `IHomeOverviewSamplerService.cs` | Background sampler interface that maintains Home metrics and chart history. |
| `IKernelBootstrapService.cs` | Entry point for Mihomo kernel preparation and download. |
| `IKernelPathService.cs` | Kernel path resolution interface. |
| `IMihomoService.cs` | High-level Mihomo controller business interface. |
| `INavigationService.cs` | Main-window page navigation interface. |
| `INetworkInfoService.cs` | Public IP and network ownership lookup interface. |
| `IProcessService.cs` | Mihomo process start/stop/diagnostic interface. |
| `IProfileService.cs` | Subscription/profile load/save/switch interface. |
| `ISystemProxyService.cs` | Windows system proxy enable/disable/sync interface. |
| `IThemeService.cs` | Theme switching and multi-window theme synchronization interface. |
| `ITrayService.cs` | System tray abstraction. |
| `IUpdateService.cs` | Update check, download, and installation interface. |

### Implementations/

| Implementation | Description |
| - | - |
| `AppLogService.cs` | In-memory application log source used by the UI and file logger. |
| `AppSettingsService.cs` | Local app settings persistence implementation. |
| `GeoDataService.cs` | Calls the GeoData download script and validates GeoData availability. |
| `HomeChartStateService.cs` | Stores recent Home chart samples and axis cache. |
| `HomeOverviewSamplerService.cs` | Background Home sampler that aggregates Mihomo connections, memory usage, and chart samples. |
| `KernelBootstrapService.cs` | Ensures the Mihomo kernel is available at startup. |
| `KernelPathService.cs` | Resolves the Mihomo kernel path used by the app. |
| `MihomoService.cs` | High-level Mihomo operations such as proxy groups, connections, config apply, and version retrieval. |
| `NavigationService.cs` | Maps routes to pages and view models in the main window. |
| `NetworkInfoService.cs` | Home dashboard network information lookup implementation. |
| `ProcessService.cs` | Mihomo process startup, shutdown, reuse, and failure diagnostic implementation. |
| `ProfileService.cs` | Subscription/profile load, save, delete, and activation implementation. |
| `SystemProxyService.cs` | System proxy registry synchronization implementation. |
| `ThemeService.cs` | Theme synchronization and backdrop application for the main window and auxiliary windows. |
| `TrayService.cs` | System tray icon and menu implementation. |
| `UpdateService.cs` | Checks GitHub Releases, downloads `.msix`, and launches App Installer. |

### Implementations/Config/

| File | Description |
| - | - |
| `ConfigService.cs` | Core manager for profile workspaces, `source.yaml`, `mixin.yaml`, `runtime.yaml`, and rule override files. |

## ViewModels/

| File | Description |
| - | - |
| `MainViewModel.cs` | Main window navigation state, route history, and sidebar selection logic. |
| `HomeViewModel.cs` | Home dashboard logic for metrics, charts, network info, and system info. |
| `ProfilesViewModel.cs` | Subscription/profile management and switching logic. |
| `ProxiesViewModel.cs` | Proxy groups, nodes, delay testing, and selection logic. |
| `ConnectionsViewModel.cs` | Connections page list, close connection, search, and refresh logic. |
| `LogsViewModel.cs` | Logs page filtering, copying, and theme-aware coloring logic. |
| `RulesViewModel.cs` | Runtime rule listing, search, toggle, and immediate-apply logic. |
| `SettingsViewModel.cs` | Settings page logic for GeoData updates, port settings, themes, updates, and app configuration. |

## Views/

### Main window and auxiliary windows

| File | Description |
| - | - |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Lightweight main-window host that manages the window lifecycle, minimize-time freeze, restore, and shell rebuild. |
| `MainShellControl.xaml` / `MainShellControl.xaml.cs` | Main navigation shell that owns `NavigationView + Frame` and top-level page routing. |
| `PortSettingsWindow.xaml` / `PortSettingsWindow.xaml.cs` | Standalone port settings window. |

### Views/Pages/

| Page/File | Description |
| - | - |
| `HomePage.xaml` / `HomePage.xaml.cs` | Home dashboard that shows connection metrics, traffic charts, network info, and system info. |
| `ProfilesPage.xaml` / `ProfilesPage.xaml.cs` | Subscription/profile management page. |
| `ProxiesPage.xaml` / `ProxiesPage.xaml.cs` | Proxy group and proxy node selection page. |
| `ConnectionsPage.xaml` / `ConnectionsPage.xaml.cs` | Connection list, search, close-connection, and traffic summary page. |
| `LogsPage.xaml` / `LogsPage.xaml.cs` | Runtime log viewer page. |
| `RulesPage.xaml` / `RulesPage.xaml.cs` | Runtime rules list, search, and enable/disable page. |
| `SettingsPage.xaml` / `SettingsPage.xaml.cs` | App settings, GeoData, kernel, updates, and runtime configuration page. |
| `IShellFreezablePage.cs` | Interface that lets pages release UI and data references before the shell freezes. |


## i18n/

| Folder | Description |
| - | - |
| `en-US/` | English resource folder containing `Resources.resw` and the English help document. |
| `zh-Hans/` | Simplified Chinese resource folder containing `Resources.resw`, README, and the Simplified Chinese help document. |
| `zh-Hant/` | Traditional Chinese resource folder containing `Resources.resw`, README, and the Traditional Chinese help document. |

## Build/

| File | Description |
| - | - |
| `DownloadKernel.ps1` | PowerShell script that downloads or refreshes the Mihomo kernel. |
| `DownloadGeoData.ps1` | PowerShell script that downloads or refreshes `geoip.metadb`, `geoip.dat`, and `geosite.dat`. |

## Additional Notes

- The repo layout mainly describes the **source structure** and **packaging structure**.
- Runtime-generated profiles, kernel copies, GeoData, logs, and user settings live under user directories and are not all committed to the repo.
- The UI follows a `Views + ViewModels + Services + Models` MVVM split, while config orchestration and Mihomo runtime flows are concentrated in `Services`, `Helpers`, and `Background`.
- The current main-window architecture uses a lightweight host plus an unloadable shell: when minimized, the WinUI shell can be unloaded while the background Home sampler continues to maintain chart history and dashboard state.
