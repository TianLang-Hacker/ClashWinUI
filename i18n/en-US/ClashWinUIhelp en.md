# ClashWinUI Project Structure Overview

## Root Directory Files

|File/Folder|Description|
|-|-|
|App.xaml|The XAML entry point of the application, defining global resources (such as styles and themes).|
|App.xaml.cs|Application code-behind: configures the dependency injection container, sets up global exception handling, initializes logging, detects single instance, and starts the system tray service.|
|Package.appxmanifest|WinUI 3 application manifest file, defining the package name, capabilities (like network permissions), icons, splash screen, etc.|
|Assets/|Stores static resource files required by the application.|

### Assets/

|File|Description|
|-|-|
|icon.png|Application icon (used in the system tray, taskbar, etc.).|
|(Other images/fonts)|Such as background images, button icons, custom fonts, etc.|

## Kernels/

Stores Clash / mihomo kernel executables. This folder is excluded from version control and is automatically downloaded by the build script.

|File|Description|
|-|-|
|.gitkeep|Used solely to retain an empty directory so Git can track this folder.|

## Common/

Stores global constants and common helper classes.

|File|Description|
|-|-|
|AppConstants.cs|Static class defining constants like the default mihomo port, API paths, version numbers, and kernel download URLs.|
|ObservableExtensions.cs|Extension methods for System.Reactive or IAsyncEnumerable to simplify reactive programming.|

## Views/

Stores all XAML user interface files, including the main window, pages, and dialogs.

|File/Folder|Description|
|-|-|
|MainWindow.xaml|The application's main window, containing layout elements like the navigation framework (NavigationView).|
|MainWindow.xaml.cs|Main window code-behind (usually very concise, only initializing components and binding the data context).|
|Pages/|Stores the application's main pages (one XAML + code-behind per page).|
|├─ HomePage.xaml|Home page: displays proxy nodes, traffic charts, switches, etc.|
|├─ HomePage.xaml.cs|Home page UI implementation: supplementary UI interaction code for proxy nodes, traffic charts, switches, etc. Business logic is placed in ViewModels/HomeViewModel.cs.|
|├─ SettingsPage.xaml|Settings page: configures application options, kernel parameters, themes, etc.|
|├─ SettingsPage.xaml.cs|Settings page UI implementation: supplementary UI interaction code for application options, kernel parameters, themes, etc. Business logic is placed in /ViewModels/SettingsViewModel.cs.|
|Dialogs/|Stores custom dialogs (XAML + ViewModel appearing in pairs).|
|├─ ExternalOpenDialog.xaml|Example: External link opening confirmation dialog.|
|├─ ExternalOpenDialogViewModel.cs|The view model for this dialog.|

## ViewModels/

Implements the View Model layer of MVVM, responsible for business logic and state management.

|File|Description|
|-|-|
|ViewModelBase.cs|View model base class, typically inheriting from ObservableRecipient or ObservableObject (CommunityToolkit.Mvvm), implementing property change notifications, message pub/sub, etc.|
|MainViewModel.cs|View model for the main window, managing global state (like the current page, tray interaction commands).|
|HomeViewModel.cs|Home page view model: handles the node list, traffic data, proxy switches, etc.|
|SettingsViewModel.cs|Settings page view model: loads/saves configurations, theme switching, kernel update triggers, etc.|

## Models/

Defines data entities and state objects.

|File|Description|
|-|-|
|MihomoStatus.cs|Represents the kernel running status (whether it's running, current mode, memory/CPU usage, etc.).|
|ProxyNode.cs|Proxy node information (name, type, latency, traffic statistics, etc.).|
|AppConfig.cs|Application configuration (user settings, UI preferences, kernel paths, etc.).|

## Services/

Core service layer, encapsulating all external interactions and business operations.

### Interfaces/

Defines service interfaces to facilitate dependency injection and unit testing.

|Interface|Description|
|-|-|
|IMihomoService.cs|High-level business interface: switching nodes, updating rules, getting status, etc., depending on the underlying API client.|
|IConfigService.cs|Configuration management: reading/writing config files, validation, backup, and restore.|
|IProcessService.cs|Process management: starting/stopping the kernel, elevation requests, process monitoring.|
|INavigationService.cs|Page navigation service, called by ViewModels to switch pages.|
|IDialogService.cs|Dialog service, displays message boxes and custom dialogs.|
|IUpdateService.cs|Kernel update service: checking for new versions, downloading, and replacing the kernel.|
|ITrayService.cs|System tray service: initializing the tray icon, displaying the menu, and responding to clicks.|
|ILoggerService.cs|(Optional) Custom logging interface, if the built-in Microsoft.Extensions.Logging interface is not used.|

### Implementations/

Concrete implementations of the interfaces.

|Implementation Class|Description|
|-|-|
|MihomoService.cs|Implements IMihomoService, calls MihomoApiClient to complete business logic, and handles state updates.|
|MihomoApiClient.cs|Underlying API communication: encapsulates HTTP requests and WebSocket connections, handles Token authentication, heartbeats, and reconnections.|
|ProcessService.cs|Implements IProcessService: starts the kernel via System.Diagnostics.Process, supports elevation (runas), listens for process exit events, and provides health check methods.|
|NavigationService.cs|Implements INavigationService: Frame-based navigation, supports parameter passing.|
|DialogService.cs|Implements IDialogService: uses ContentDialog to show dialogs, supports asynchronously waiting for results.|
|UpdateService.cs|Implements IUpdateService: checks for versions from sources like GitHub Releases, downloads files, and verifies hashes.|
|TrayService.cs|Implements ITrayService: uses the H.NotifyIcon library to create the tray icon and context menu, handles menu click events.|

### Config/

Configuration management implementations, split by module.

|File|Description|
|-|-|
|ConfigService.cs|Main configuration service, combining the validator and backup manager.|
|ConfigValidator.cs|Configuration validation logic (YAML format, required fields, etc.).|
|ConfigBackupManager.cs|Automatic backup and restoration of configurations to prevent corruption.|

## Background/

Stores background running tasks and monitoring components, usually running on non-UI threads.

|File|Description|
|-|-|
|ProcessMonitor.cs|Kernel process monitoring: monitors process exit events, automatically restarts based on policies, and notifies relevant services.|
|MihomoEventSubscriber.cs|WebSocket event subscription: connects to mihomo's /traffic, /logs, etc., endpoints, converting pushed data into observable streams for ViewModels to subscribe to.|
|HealthChecker.cs|Health check: periodically sends requests to the mihomo API to check if the kernel is responsive, triggering notifications or automatic recovery upon exceptions.|

## Exceptions/

Custom exceptions and global exception handling logic.

|File|Description|
|-|-|
|GlobalExceptionHandler.cs|Global unhandled exception capture: registered to AppDomain.UnhandledException and DispatcherUnhandledException, logs and displays user-friendly prompts.|
|KernelException.cs|Kernel-related custom exceptions (like startup failures, API timeouts, etc.), facilitating error type differentiation.|

## Logging/

Logging-related components (if using a custom logging provider).

|File|Description|
|-|-|
|FileLoggerProvider.cs|Custom file logger provider, implementing ILoggerProvider, writing logs to local files.|

## Helpers/

General utility helper classes.

|File|Description|
|-|-|
|FileHelper.cs|File operation helpers: reading/writing files, copying, deleting, path handling, etc.|
|JsonHelper.cs|JSON serialization / deserialization wrappers (based on System.Text.Json or Newtonsoft.Json).|
|LocalizedStrings.cs|Internationalization helpers: provides binding access to resource strings, supporting dynamic language switching.|
|(Others)|May contain custom helper classes migrated from the original Infrastructure.|

## i18n/

Multi-language resource folder.

|Folder|Description|
|-|-|
|en-US/|US English resources: contains the Resources.resw file, defining all English text.|
|zh-Hans/|Simplified Chinese resources.|
|zh-Hant/|Traditional Chinese resources.|

## Build/

Stores scripts and configuration files related to the build.

|File|Description|
|-|-|
|DownloadKernel.ps1|PowerShell script, automatically downloads a specified version of the mihomo kernel before compilation, supports version locking and hash verification.|
|(Other subsequent build-related files)|Post-build processing scripts, code signing tools, etc.|