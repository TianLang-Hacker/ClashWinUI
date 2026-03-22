# ClashWinUI 项目结构说明

## 根目录与主要文件

| 文件/目录 | 说明 |
| - | - |
| `App.xaml` | 应用入口 XAML，定义全局资源、样式和主题资源。 |
| `App.xaml.cs` | 应用启动代码，负责 Host/依赖注入注册、启动流程、未处理异常兜底、托盘初始化，以及 Mihomo/GeoData 的启动链。 |
| `Package.appxmanifest` | WinUI 3 打包清单，定义包信息、权限、图标和启动行为。 |
| `Properties/` | 项目属性目录，当前主要包含 `launchSettings.json`。 |
| `Assets/` | 打包图标、启动画面和商店资源图片。 |
| `Build/` | 构建相关脚本，例如下载 Mihomo 内核和 GeoData。 |
| `i18n/` | 多语言资源与帮助文档。 |
| `Kernels/` | 仓库中的内核占位目录，当前仅保留 `.gitkeep` 以便版本控制跟踪。 |
| `bin/`、`obj/` | 本地构建输出目录。 |
| `AppPackages/`、`BundleArtifacts/` | 打包产物和打包辅助输出目录。 |

### Assets/

| 文件 | 说明 |
| - | - |
| `ClashWinUI.ico` | 应用主图标。 |
| `SplashScreen.scale-200.png` | 启动画面资源。 |
| `Square150x150Logo.scale-200.png` 等 | Windows 包图标和磁贴资源。 |

## Common/

| 文件 | 说明 |
| - | - |
| `AppConstants.cs` | 全局常量，例如默认控制器端口、路由键和应用级常量。 |
| `ObservableExtensions.cs` | 可观察对象和异步流的辅助扩展方法。 |

## Converters/

`Converters/` 存放页面绑定转换器，负责把布尔值、字符串或延迟等级转换成 `Brush`、`Visibility` 等 UI 需要的类型。

常见文件包括：

| 文件 | 说明 |
| - | - |
| `BooleanToCardBackgroundBrushConverter.cs` | 根据布尔状态切换卡片背景。 |
| `BooleanToCardBorderBrushConverter.cs` | 根据布尔状态切换卡片边框。 |
| `BooleanToVisibilityConverter.cs` | 布尔值转可见性。 |
| `StringToVisibilityConverter.cs` | 字符串为空/非空转可见性。 |
| `ProxyDelayLevelToBrushConverter.cs` | 根据节点延迟等级给出对应颜色。 |

## Background/

| 文件 | 说明 |
| - | - |
| `HealthChecker.cs` | 周期性检查 Mihomo 控制器可用性。 |
| `MihomoEventSubscriber.cs` | 订阅 Mihomo 推送事件，供页面或服务层消费。 |
| `ProcessMonitor.cs` | 监控 Mihomo 进程生命周期并辅助恢复。 |

## Exceptions/

| 文件 | 说明 |
| - | - |
| `GlobalExceptionHandler.cs` | 全局异常捕获与日志记录辅助。 |
| `KernelException.cs` | Mihomo 内核相关的自定义异常。 |

## Helpers/

`Helpers/` 存放跨页面、跨服务复用的工具类和适配逻辑。

| 文件 | 说明 |
| - | - |
| `FileHelper.cs` | 文件读写与路径处理辅助。 |
| `GeoDataStatusTextHelper.cs` | 根据 GeoData 结果生成用户可读状态文案。 |
| `JsonHelper.cs` | JSON 读写辅助。 |
| `LiveChartsBootstrapper.cs` | 首页 LiveCharts 图表的懒初始化入口。 |
| `LocalizedStrings.cs` | 本地化字符串访问包装器。 |
| `LogLevelToBrushConverter.cs` | 按日志等级映射文本颜色。 |
| `ProfileCompatibilityChecker.cs` | 校验配置文件/订阅与当前运行环境的兼容性。 |
| `ProxyConfigParser.cs` | 解析代理节点配置。 |
| `ProxyGroupParser.cs` | 解析代理组和成员结构。 |
| `ShareLinkSubscriptionConverter.cs` | 将分享链接订阅转换成 Mihomo YAML。 |
| `SubscriptionContentNormalizer.cs` | 统一订阅内容编码和格式。 |

## Logging/

| 文件 | 说明 |
| - | - |
| `FileLoggerProvider.cs` | 自定义日志提供器，把日志写入本地文件。 |

## Models/

`Models/` 定义应用的状态对象、页面显示模型和运行期数据结构。

| 文件 | 说明 |
| - | - |
| `AppConfig.cs`、`CloseBehavior.cs` | 应用配置和关闭行为定义。 |
| `ConnectionEntry.cs`、`ConnectionsColumnLayout.cs` | 连接页的数据行模型和列布局状态。 |
| `GeoDataAssetStatus.cs`、`GeoDataFailureKind.cs`、`GeoDataOperationKind.cs`、`GeoDataOperationResult.cs` | GeoData 下载/校验结果模型。 |
| `HomeChartSample.cs`、`PublicNetworkInfo.cs` | 首页图表采样点和公网网络信息。 |
| `LogEntry.cs` | 日志页单条日志项。 |
| `MihomoFailureDiagnostic.cs`、`MihomoFailureKind.cs`、`MihomoStatus.cs` | Mihomo 运行状态与故障诊断信息。 |
| `MixinSettings.cs`、`PortSettingsDraft.cs`、`ProfileConfigWorkspace.cs`、`ProfileItem.cs` | 配置工作区、混合配置和订阅资料模型。 |
| `ProxyGroup.cs`、`ProxyGroupLoadResult.cs`、`ProxyGroupMember.cs`、`ProxyNode.cs` | 代理页使用的节点、分组和加载结果模型。 |
| `RuntimeRuleItem.cs` | 规则页展示与启停用的运行时规则模型。 |

## Serialization/

| 文件 | 说明 |
| - | - |
| `ClashJsonContext.cs` | `System.Text.Json` 的源生成上下文，用于提高 JSON 序列化性能并统一模型注册。 |

## Services/

### Interfaces/

| 接口 | 说明 |
| - | - |
| `IAppLogService.cs` | 应用日志收集与查询接口。 |
| `IAppSettingsService.cs` | 应用设置读写接口。 |
| `IConfigService.cs` | 订阅配置、Mixin、Runtime 与规则开关管理接口。 |
| `IDialogService.cs` | 对话框显示接口。 |
| `IGeoDataService.cs` | GeoData 准备、刷新与状态查询接口。 |
| `IKernelBootstrapService.cs` | Mihomo 内核准备与下载入口。 |
| `IKernelPathService.cs` | 内核路径解析接口。 |
| `ILoggerService.cs` | 日志抽象接口。 |
| `IMihomoService.cs` | Mihomo 控制器高层业务接口。 |
| `INavigationService.cs` | 主窗口页面导航接口。 |
| `INetworkInfoService.cs` | 公网 IP 和网络归属地查询接口。 |
| `IProcessService.cs` | Mihomo 进程启动、停止与诊断接口。 |
| `IProfileService.cs` | 订阅资料读取、保存、切换接口。 |
| `ISystemProxyService.cs` | Windows 系统代理启停与同步接口。 |
| `IThemeService.cs` | 主题切换与多窗口主题同步接口。 |
| `ITrayService.cs` | 系统托盘接口。 |
| `IUpdateService.cs` | 更新检查与下载接口。 |

### Implementations/

| 实现 | 说明 |
| - | - |
| `AppLogService.cs` | 应用内存日志与页面日志源实现。 |
| `AppSettingsService.cs` | 本地应用设置读写实现。 |
| `DialogService.cs` | 基于 WinUI 对话框的实现。 |
| `GeoDataService.cs` | 调用下载脚本并检查 GeoData 状态。 |
| `KernelBootstrapService.cs` | 启动时准备 Mihomo 内核。 |
| `KernelPathService.cs` | 解析当前应使用的 Mihomo 内核路径。 |
| `MihomoApiClient.cs` | 底层控制器 API 通信组件。 |
| `MihomoService.cs` | Mihomo 相关高层业务实现，例如代理组、连接、规则应用与版本读取。 |
| `NavigationService.cs` | 主窗口各页面与 ViewModel 的导航映射。 |
| `NetworkInfoService.cs` | 首页公网 IP 与网络信息查询实现。 |
| `ProcessService.cs` | Mihomo 进程启动、停止、复用和失败诊断实现。 |
| `ProfileService.cs` | 订阅资料加载、保存、删除和切换实现。 |
| `SystemProxyService.cs` | 系统代理注册表同步实现。 |
| `ThemeService.cs` | 主窗口和子窗口的主题同步实现。 |
| `TrayService.cs` | 系统托盘图标和菜单实现。 |
| `UpdateService.cs` | 更新检查与下载实现。 |

### Implementations/Config/

| 文件 | 说明 |
| - | - |
| `ConfigService.cs` | 配置工作区、`source.yaml`、`mixin.yaml`、`runtime.yaml` 与规则覆盖文件的核心管理实现。 |
| `ConfigValidator.cs` | 配置合法性检查。 |
| `ConfigBackupManager.cs` | 配置备份与恢复辅助。 |

## ViewModels/

| 文件 | 说明 |
| - | - |
| `ViewModelBase.cs` | 视图模型基础能力。 |
| `MainViewModel.cs` | 主窗口导航状态与侧边栏路由。 |
| `HomeViewModel.cs` | 首页总览、图表、网络信息、系统信息逻辑。 |
| `ProfilesViewModel.cs` | 订阅资料管理与切换逻辑。 |
| `ProxiesViewModel.cs` | 代理组、节点、测速与选择逻辑。 |
| `ConnectionsViewModel.cs` | 连接页列表、关闭连接、搜索与刷新逻辑。 |
| `LogsViewModel.cs` | 日志页筛选、复制与主题色逻辑。 |
| `RulesViewModel.cs` | 运行时规则列表、搜索、启停与立即生效逻辑。 |
| `SettingsViewModel.cs` | 设置页、GeoData 更新、端口设置、主题与应用配置逻辑。 |

## Views/

### 主窗口与子窗口

| 文件 | 说明 |
| - | - |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | 应用主窗口与导航宿主。 |
| `PortSettingsWindow.xaml` / `PortSettingsWindow.xaml.cs` | 独立端口设置窗口。 |

### Views/Pages/

| 页面 | 说明 |
| - | - |
| `HomePage.xaml` / `HomePage.xaml.cs` | 首页总览仪表盘，显示连接数、流量、图表、网络信息、系统信息等。 |
| `ProfilesPage.xaml` / `ProfilesPage.xaml.cs` | 订阅资料页面。 |
| `ProxiesPage.xaml` / `ProxiesPage.xaml.cs` | 代理组与节点选择页面。 |
| `ConnectionsPage.xaml` / `ConnectionsPage.xaml.cs` | 连接列表、搜索、关闭连接和连接统计页面。 |
| `LogsPage.xaml` / `LogsPage.xaml.cs` | 运行日志查看页面。 |
| `RulesPage.xaml` / `RulesPage.xaml.cs` | 运行时规则展示、搜索和开关页面。 |
| `SettingsPage.xaml` / `SettingsPage.xaml.cs` | 应用设置、GeoData、内核和运行配置页面。 |

> 当前仓库没有单独的 `Dialogs/` 目录；对话框能力主要通过 `IDialogService` 和页面内的 `ContentDialog` 组织。

## i18n/

| 目录 | 说明 |
| - | - |
| `en-US/` | 英文资源目录，包含 `Resources.resw` 和英文帮助文档。 |
| `zh-Hans/` | 简体中文资源目录，包含 `Resources.resw` 和简体中文帮助文档。 |
| `zh-Hant/` | 繁体中文资源目录，包含 `Resources.resw` 和繁体中文帮助文档。 |

## Build/

| 文件 | 说明 |
| - | - |
| `DownloadKernel.ps1` | 下载或更新 Mihomo 内核的 PowerShell 脚本。 |
| `DownloadGeoData.ps1` | 下载或刷新 `geoip.metadb`、`geoip.dat`、`geosite.dat` 的 PowerShell 脚本。 |

## 结构补充说明

- 仓库中的目录主要描述的是**源码结构**和**打包结构**。
- 运行时生成的订阅资料、内核副本、GeoData、日志和用户设置会存放在用户目录，不会全部直接提交到仓库中。
- 页面层遵循 `Views + ViewModels + Services + Models` 的 MVVM 分层，配置与 Mihomo 运行链则集中在 `Services`、`Helpers` 和 `Background` 中实现。
