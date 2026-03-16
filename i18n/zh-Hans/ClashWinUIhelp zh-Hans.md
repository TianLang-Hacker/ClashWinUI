# ClashWinUI 项目结构说明

## 根目录文件

|文件/文件夹|说明|
|-|-|
|App.xaml|应用程序的 XAML 入口，定义全局资源（如样式、主题）。|
|App.xaml.cs|应用程序代码隐藏：配置依赖注入容器、设置全局异常处理、初始化日志、检测单实例、启动托盘服务。|
|Package.appxmanifest|WinUI 3 应用清单文件，定义包名称、功能（如网络权限）、图标、启动窗口等。|
|Assets/|存放应用所需的静态资源文件。|

### Assets/

|文件|说明|
|-|-|
|icon.png|应用图标（托盘、任务栏等使用）。|
|(其他图片/字体)|如背景图、按钮图标、自定义字体等。|

## Kernels/

存放 Clash / mihomo 内核可执行文件。此文件夹不加入版本控制，由构建脚本自动下载。

|文件|说明|
|-|-|
|.gitkeep|仅用于保留空目录，使 Git 能追踪此文件夹。|

## Common/

存放全局常量和公共辅助类。

|文件|说明|
|-|-|
|AppConstants.cs|静态类，定义 mihomo 默认端口、API 路径、版本号、内核下载地址等常量。|
|ObservableExtensions.cs|针对 System.Reactive 或 IAsyncEnumerable 的扩展方法，简化响应式编程。|

## Views/

存放所有 XAML 用户界面文件，包括主窗口、页面和对话框。

|文件/文件夹|说明|
|-|-|
|MainWindow.xaml|应用主窗口，包含导航框架（NavigationView）等布局元素。|
|MainWindow.xaml.cs|主窗口代码隐藏（通常很精简，仅初始化组件和绑定数据上下文）。|
|Pages/|存放应用的主要页面（每个页面一个 XAML + 代码隐藏）。|
|├─ HomePage.xaml|主页：显示代理节点、流量图表、开关等。|
|├─ HomePage.xaml.cs|主页UI实现：显示代理节点、流量图表、开关等的补充UI交互代码，业务逻辑等放到ViewModels/HomeViewModel.cs|
|├─ SettingsPage.xaml|设置页：配置应用选项、内核参数、主题等。|
|├─ SettingsPage.xaml.cs|设置页UI实现：配置应用选项、内核参数、主题等的补充UI交互代码，业务逻辑等放到/ViewModels/SettingsViewModel.cs|
|Dialogs/|存放自定义对话框（XAML + ViewModel 成对出现）。|
|├─ ExternalOpenDialog.xaml|示例：外部链接打开确认对话框。|
|├─ ExternalOpenDialogViewModel.cs|该对话框的视图模型。|

## ViewModels/

实现 MVVM 的视图模型层，负责业务逻辑和状态管理。

|文件|说明|
|-|-|
|ViewModelBase.cs|视图模型基类，通常继承自 ObservableRecipient 或 ObservableObject（CommunityToolkit.Mvvm），实现属性更改通知、消息收发等。|
|MainViewModel.cs|主窗口的视图模型，管理全局状态（如当前页面、托盘交互命令）。|
|HomeViewModel.cs|主页视图模型：处理节点列表、流量数据、代理开关等。|
|SettingsViewModel.cs|设置页视图模型：加载/保存配置、主题切换、内核更新触发等。|

## Models/

定义数据实体和状态对象。

|文件|说明|
|-|-|
|MihomoStatus.cs|表示内核运行状态（是否运行、当前模式、内存/CPU 使用等）。|
|ProxyNode.cs|代理节点信息（名称、类型、延迟、流量统计等）。|
|AppConfig.cs|应用配置（用户设置、UI 偏好、内核路径等）。|

## Services/

核心服务层，封装所有与外部交互和业务操作。

### Interfaces/

定义服务接口，便于依赖注入和单元测试。

|接口|说明|
|-|-|
|IMihomoService.cs|高级业务接口：切换节点、更新规则、获取状态等，依赖底层 API 客户端。|
|IConfigService.cs|配置管理：读取/写入配置文件、校验、备份恢复。|
|IProcessService.cs|进程管理：启动/停止内核、提权请求、进程监控。|
|INavigationService.cs|页面导航服务，供 ViewModel 调用切换页面。|
|IDialogService.cs|对话框服务，显示消息框、自定义对话框。|
|IUpdateService.cs|内核更新服务：检查新版本、下载、替换内核。|
|ITrayService.cs|系统托盘服务：初始化托盘图标、显示菜单、响应点击。|
|ILoggerService.cs|（可选）自定义日志接口，如果未使用 Microsoft.Extensions.Logging 的内置接口。|

### Implementations/

接口的具体实现。

|实现类|说明|
|-|-|
|MihomoService.cs|实现 IMihomoService，调用 MihomoApiClient 完成业务逻辑，并处理状态更新。|
|MihomoApiClient.cs|底层 API 通信：封装 HTTP 请求和 WebSocket 连接，处理 Token 鉴权、心跳、重连。|
|ProcessService.cs|实现 IProcessService：通过 System.Diagnostics.Process 启动内核，支持提权（runas），监听进程退出事件，提供健康检查方法。|
|NavigationService.cs|实现 INavigationService：基于 Frame 的导航，支持参数传递。|
|DialogService.cs|实现 IDialogService：使用 ContentDialog 显示对话框，支持异步等待结果。|
|UpdateService.cs|实现 IUpdateService：从 GitHub Releases 等源检查版本，下载文件并校验哈希。|
|TrayService.cs|实现 ITrayService：使用 H.NotifyIcon 库创建托盘图标和上下文菜单，处理菜单点击事件。|

### Config/

配置管理相关实现，按模块拆分。

|文件|说明|
|-|-|
|ConfigService.cs|主配置服务，组合校验器和备份管理器。|
|ConfigValidator.cs|配置校验逻辑（YAML 格式、必要字段等）。|
|ConfigBackupManager.cs|自动备份与恢复配置，防止配置损坏。|

## Background/

存放后台运行的任务和监控组件，通常运行在非 UI 线程。

|文件|说明|
|-|-|
|ProcessMonitor.cs|内核进程监控：监视进程退出事件，根据策略自动重启，并通知相关服务。|
|MihomoEventSubscriber.cs|WebSocket 事件订阅：连接 mihomo 的 /traffic、/logs 等端点，将推送的数据转换为可观察流，供 ViewModel 订阅。|
|HealthChecker.cs|健康检查：定期向 mihomo API 发送请求，检查内核是否响应，异常时触发通知或自动恢复。|

## Exceptions/

自定义异常和全局异常处理逻辑。

|文件|说明|
|-|-|
|GlobalExceptionHandler.cs|全局未处理异常捕获：注册到 AppDomain.UnhandledException 和 DispatcherUnhandledException，记录日志并显示友好提示。|
|KernelException.cs|内核相关自定义异常（如启动失败、API 超时等），便于区分错误类型。|

## Logging/

日志记录相关组件（如果使用自定义日志提供程序）。

|文件|说明|
|-|-|
|FileLoggerProvider.cs|自定义文件日志提供程序，实现 ILoggerProvider，将日志写入本地文件。|

## Helpers/

通用辅助工具类。

|文件|说明|
|-|-|
|FileHelper.cs|文件操作辅助：读写文件、复制、删除、路径处理等。|
|JsonHelper.cs|JSON 序列化 / 反序列化封装（基于 System.Text.Json 或 Newtonsoft.Json）。|
|LocalizedStrings.cs|国际化辅助：提供资源字符串的绑定访问，支持动态语言切换。|
|（其他）|可能包含从原 Infrastructure 迁移过来的自定义辅助类。|

## i18n/

多语言资源文件夹。

|文件夹|说明|
|-|-|
|en-US/|美国英语资源：包含 Resources.resw 文件，定义所有英文文本。|
|zh-Hans/|简体中文资源。|
|zh-Hant/|繁体中文资源。|

## Build/

存放与构建相关的脚本和配置文件。

|文件|说明|
|-|-|
|DownloadKernel.ps1|PowerShell 脚本，在编译前自动下载指定版本的 mihomo 内核，支持版本锁定和哈希校验。|
|（后续的其他构建相关文件）|构建后处理脚本、代码签名工具等。|
