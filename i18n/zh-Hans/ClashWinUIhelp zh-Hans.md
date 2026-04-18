# ClashWinUI 项目结构说明

## 根目录与主要文件

| 文件/目录 | 说明 |
| - | - |
| `App.xaml` | 应用入口 XAML，定义全局资源、样式和主题资源。 |
| `App.xaml.cs` | 应用启动代码，负责 Host/依赖注入注册、启动流程、未处理异常兜底、托盘初始化、更新检查启动，以及 Mihomo/GeoData 的启动链。 |
| `Package.appxmanifest` | WinUI 3 打包清单，定义包信息、权限、图标和启动行为。 |
| `Properties/` | 项目属性目录，包含 `launchSettings.json` 和发布配置。 |
| `Assets/` | 打包图标、启动画面和商店资源图片。 |
| `Build/` | 构建相关脚本，例如下载 Mihomo 内核和 GeoData。 |
| `i18n/` | 多语言资源、说明文档和本地化帮助文件。 |
| `Kernels/` | 仓库中的内核占位目录，当前仅保留 `.gitkeep` 以便版本控制跟踪。 |
| `bin/`、`obj/` | 本地构建输出目录。 |
| `AppPackages/`、`BundleArtifacts/` | 打包产物和打包辅助输出目录。 |

## Common/

| 文件 | 说明 |
| - | - |
| `AppConstants.cs` | 全局常量，例如默认控制器端口、路由键和应用级常量。 |

## Converters/

`Converters/` 存放页面绑定转换器，负责把布尔值、字符串或延迟等级转换成 `Brush`、`Visibility` 等 UI 需要的类型。

| 文件 | 说明 |
| - | - |
| `BooleanToCardBackgroundBrushConverter.cs` | 根据布尔状态切换卡片背景。 |
| `BooleanToCardBorderBrushConverter.cs` | 根据布尔状态切换卡片边框。 |
| `BooleanToVisibilityConverter.cs` | 布尔值转可见性。 |
| `StringToVisibilityConverter.cs` | 字符串为空/非空转可见性。 |
| `ProxyDelayLevelToBrushConverter.cs` | 根据节点延迟等级给出对应颜色。 |

## Helpers/

`Helpers/` 存放跨页面、跨服务复用的工具类和适配逻辑。

| 文件 | 说明 |
| - | - |
| `AppPackageInfoHelper.cs` | 统一读取包标识、版本号、发布者和架构信息，供设置页与更新逻辑使用。 |
| `GeoDataStatusTextHelper.cs` | 根据 GeoData 结果生成用户可读状态文案。 |
| `LiveChartsBootstrapper.cs` | 首页 LiveCharts 图表的懒初始化入口。 |
| `LocalizedStrings.cs` | 本地化字符串访问包装器。 |
| `PageMemoryTrimHelper.cs` | 重页面离页、Shell 冻结后触发 GC 与工作集收缩。 |
| `ProfileCompatibilityChecker.cs` | 校验配置文件/订阅与当前运行环境的兼容性。 |
| `ProxyConfigParser.cs` | 解析代理节点配置。 |
| `ProxyGroupParser.cs` | 解析代理组和成员结构。 |
| `ShareLinkSubscriptionConverter.cs` | 将分享链接订阅转换成 Mihomo YAML。 |
| `SubscriptionContentNormalizer.cs` | 统一订阅内容编码和格式。 |

## Models/

`Models/` 定义应用状态对象、页面显示模型和运行期数据结构。

| 文件 | 说明 |
| - | - |
| `CloseBehavior.cs` | 应用关闭行为定义。 |
| `ConnectionEntry.cs`、`ConnectionsColumnLayout.cs` | 连接页的数据行模型和列布局状态。 |
| `GeoDataAssetStatus.cs`、`GeoDataFailureKind.cs`、`GeoDataOperationKind.cs`、`GeoDataOperationResult.cs` | GeoData 下载/校验结果模型。 |
| `HomeChartSample.cs`、`HomeChartState.cs`、`HomeOverviewState.cs` | 首页图表采样点、图表历史缓存和首页总览快照模型。 |
| `LogEntry.cs` | 日志页单条日志项。 |
| `MihomoFailureDiagnostic.cs`、`MihomoFailureKind.cs` | Mihomo 故障诊断信息。 |
| `MixinSettings.cs`、`PortSettingsDraft.cs`、`ProfileConfigWorkspace.cs`、`ProfileItem.cs` | 配置工作区、混合配置和订阅资料模型。 |
| `ProxyGroup.cs`、`ProxyGroupLoadResult.cs`、`ProxyGroupMember.cs`、`ProxyNode.cs` | 代理页使用的节点、分组和加载结果模型。 |
| `PublicNetworkInfo.cs` | 首页公网网络信息模型。 |
| `RuntimeRuleItem.cs` | 规则页展示与启停用的运行时规则模型。 |
| `UpdateState.cs`、`UpdateStatus.cs` | 更新检查、下载和安装链的状态模型。 |

## Serialization/

| 文件 | 说明 |
| - | - |
| `ClashJsonContext.cs` | `System.Text.Json` 的源生成上下文，用于提高 JSON 序列化性能并统一模型注册。 |


### Interfaces/

| 接口 | 说明 |
| - | - |
| `IAppLogService.cs` | 应用日志收集与查询接口。 |
| `IAppSettingsService.cs` | 应用设置读写接口。 |
| `IConfigService.cs` | 订阅配置、Mixin、Runtime 与规则开关管理接口。 |
| `IGeoDataService.cs` | GeoData 准备、刷新与状态查询接口。 |
| `IHomeChartStateService.cs` | 首页图表历史缓存接口。 |
| `IHomeOverviewSamplerService.cs` | 首页后台采样接口，负责持续维护连接、速度、内存和图表历史。 |
| `IKernelBootstrapService.cs` | Mihomo 内核准备与下载入口。 |
| `IKernelPathService.cs` | 内核路径解析接口。 |
| `IMihomoService.cs` | Mihomo 控制器高层业务接口。 |
| `INavigationService.cs` | 主窗口页面导航接口。 |
| `INetworkInfoService.cs` | 公网 IP 和网络归属地查询接口。 |
| `IProcessService.cs` | Mihomo 进程启动、停止与诊断接口。 |
| `IProfileService.cs` | 订阅资料读取、保存、切换接口。 |
| `ISystemProxyService.cs` | Windows 系统代理启停与同步接口。 |
| `IThemeService.cs` | 主题切换与多窗口主题同步接口。 |
| `ITrayService.cs` | 系统托盘接口。 |
| `IUpdateService.cs` | 更新检查、下载与安装接口。 |

### Implementations/

| 实现 | 说明 |
| - | - |
| `AppLogService.cs` | 应用内存日志与页面日志源实现。 |
| `AppSettingsService.cs` | 本地应用设置读写实现。 |
| `GeoDataService.cs` | 调用下载脚本并检查 GeoData 状态。 |
| `HomeChartStateService.cs` | 保存首页图表最近样本和轴上限缓存。 |
| `HomeOverviewSamplerService.cs` | 首页后台采样实现，持续聚合 Mihomo 连接统计、内存和图表样本。 |
| `KernelBootstrapService.cs` | 启动时准备 Mihomo 内核。 |
| `KernelPathService.cs` | 解析当前应使用的 Mihomo 内核路径。 |
| `MihomoService.cs` | Mihomo 相关高层业务实现，例如代理组、连接、规则应用与版本读取。 |
| `NavigationService.cs` | 主窗口各页面与 ViewModel 的导航映射。 |
| `NetworkInfoService.cs` | 首页公网 IP 与网络信息查询实现。 |
| `ProcessService.cs` | Mihomo 进程启动、停止、复用和失败诊断实现。 |
| `ProfileService.cs` | 订阅资料加载、保存、删除和切换实现。 |
| `SystemProxyService.cs` | 系统代理注册表同步实现。 |
| `ThemeService.cs` | 主窗口和子窗口的主题同步与背板应用实现。 |
| `TrayService.cs` | 系统托盘图标和菜单实现。 |
| `UpdateService.cs` | 从 GitHub Release 检查更新、下载 `.msix` 并调用 App Installer。 |

### Implementations/Config/

| 文件 | 说明 |
| - | - |
| `ConfigService.cs` | 配置工作区、`source.yaml`、`mixin.yaml`、`runtime.yaml` 与规则覆盖文件的核心管理实现。 |

## ViewModels/

| 文件 | 说明 |
| - | - |
| `MainViewModel.cs` | 主窗口导航状态、路由历史和侧边栏选中逻辑。 |
| `HomeViewModel.cs` | 首页总览、图表、网络信息、系统信息展示逻辑。 |
| `ProfilesViewModel.cs` | 订阅资料管理与切换逻辑。 |
| `ProxiesViewModel.cs` | 代理组、节点、测速与选择逻辑。 |
| `ConnectionsViewModel.cs` | 连接页列表、关闭连接、搜索与刷新逻辑。 |
| `LogsViewModel.cs` | 日志页筛选、复制与主题色逻辑。 |
| `RulesViewModel.cs` | 运行时规则列表、搜索、启停与立即生效逻辑。 |
| `SettingsViewModel.cs` | 设置页、GeoData 更新、端口设置、主题、更新与应用配置逻辑。 |

## Views/

### 主窗口与子窗口

| 文件 | 说明 |
| - | - |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | 应用主窗口的轻量宿主，负责窗口生命周期、最小化冻结、恢复和 Shell 重建。 |
| `MainShellControl.xaml` / `MainShellControl.xaml.cs` | 主窗口导航外壳，承载 `NavigationView + Frame` 和顶层页面导航。 |
| `PortSettingsWindow.xaml` / `PortSettingsWindow.xaml.cs` | 独立端口设置窗口。 |

### Views/Pages/

| 页面/文件 | 说明 |
| - | - |
| `HomePage.xaml` / `HomePage.xaml.cs` | 首页总览仪表盘，显示连接数、流量、图表、网络信息、系统信息等。 |
| `ProfilesPage.xaml` / `ProfilesPage.xaml.cs` | 订阅资料页面。 |
| `ProxiesPage.xaml` / `ProxiesPage.xaml.cs` | 代理组与节点选择页面。 |
| `ConnectionsPage.xaml` / `ConnectionsPage.xaml.cs` | 连接列表、搜索、关闭连接和连接统计页面。 |
| `LogsPage.xaml` / `LogsPage.xaml.cs` | 运行日志查看页面。 |
| `RulesPage.xaml` / `RulesPage.xaml.cs` | 运行时规则展示、搜索和开关页面。 |
| `SettingsPage.xaml` / `SettingsPage.xaml.cs` | 应用设置、GeoData、内核、更新和运行配置页面。 |
| `IShellFreezablePage.cs` | 页面在 Shell 冻结前主动释放 UI/数据引用的接口。 |


## i18n/

| 目录 | 说明 |
| - | - |
| `en-US/` | 英文资源目录，包含 `Resources.resw` 和英文帮助文档。 |
| `zh-Hans/` | 简体中文资源目录，包含 `Resources.resw`、README 和简体中文帮助文档。 |
| `zh-Hant/` | 繁体中文资源目录，包含 `Resources.resw`、README 和繁体中文帮助文档。 |

## Build/

| 文件 | 说明 |
| - | - |
| `DownloadKernel.ps1` | 下载或更新 Mihomo 内核的 PowerShell 脚本。 |
| `DownloadGeoData.ps1` | 下载或刷新 `geoip.metadb`、`geoip.dat`、`geosite.dat` 的 PowerShell 脚本。 |

## 结构补充说明

- 仓库中的目录主要描述的是**源码结构**和**打包结构**。
- 运行时生成的订阅资料、内核副本、GeoData、日志和用户设置会存放在用户目录，不会全部直接提交到仓库中。
- 页面层遵循 `Views + ViewModels + Services + Models` 的 MVVM 分层，配置与 Mihomo 运行链则集中在 `Services`、`Helpers` 与 `Background` 中实现。
- 当前主窗口采用“轻量宿主 + 可卸载 Shell”结构：最小化时会卸载 WinUI Shell，首页后台采样服务继续维护图表历史与总览数据。
