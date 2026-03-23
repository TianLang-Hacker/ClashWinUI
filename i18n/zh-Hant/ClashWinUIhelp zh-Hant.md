# ClashWinUI 專案結構說明

## 根目錄與主要檔案

| 檔案/目錄 | 說明 |
| - | - |
| `App.xaml` | 應用程式入口 XAML，定義全域資源、樣式與主題資源。 |
| `App.xaml.cs` | 應用程式啟動程式碼，負責 Host/相依注入註冊、啟動流程、未處理例外兜底、系統匣初始化、更新檢查啟動，以及 Mihomo/GeoData 的啟動鏈。 |
| `Package.appxmanifest` | WinUI 3 打包清單，定義套件資訊、權限、圖示與啟動行為。 |
| `Properties/` | 專案屬性目錄，包含 `launchSettings.json` 與發佈設定。 |
| `Assets/` | 打包圖示、啟動畫面與商店資源圖片。 |
| `Build/` | 建置相關腳本，例如下載 Mihomo 核心與 GeoData。 |
| `i18n/` | 多語系資源、說明文件與本地化幫助文件。 |
| `Kernels/` | 倉庫中的核心佔位目錄，目前僅保留 `.gitkeep` 以便版本控制追蹤。 |
| `bin/`、`obj/` | 本機建置輸出目錄。 |
| `AppPackages/`、`BundleArtifacts/` | 打包產物與打包輔助輸出目錄。 |

## Common/

| 檔案 | 說明 |
| - | - |
| `AppConstants.cs` | 全域常數，例如預設控制器連接埠、路由鍵與應用層常數。 |
| `ObservableExtensions.cs` | 可觀察物件與非同步串流的輔助擴充方法。 |

## Converters/

`Converters/` 存放頁面繫結轉換器，負責把布林值、字串或延遲等級轉成 `Brush`、`Visibility` 等 UI 需要的型別。

| 檔案 | 說明 |
| - | - |
| `BooleanToCardBackgroundBrushConverter.cs` | 根據布林狀態切換卡片背景。 |
| `BooleanToCardBorderBrushConverter.cs` | 根據布林狀態切換卡片邊框。 |
| `BooleanToVisibilityConverter.cs` | 布林值轉可見性。 |
| `StringToVisibilityConverter.cs` | 字串為空/非空轉可見性。 |
| `ProxyDelayLevelToBrushConverter.cs` | 依據節點延遲等級給出對應顏色。 |

## Background/

| 檔案 | 說明 |
| - | - |
| `HealthChecker.cs` | 週期性檢查 Mihomo 控制器是否可用。 |
| `MihomoEventSubscriber.cs` | 訂閱 Mihomo 推送事件，供頁面或服務層消費。 |
| `ProcessMonitor.cs` | 監控 Mihomo 行程生命週期並協助恢復。 |

## Exceptions/

| 檔案 | 說明 |
| - | - |
| `GlobalExceptionHandler.cs` | 全域例外捕捉與日誌記錄輔助。 |
| `KernelException.cs` | Mihomo 核心相關自訂例外。 |

## Helpers/

`Helpers/` 存放跨頁面、跨服務重用的工具類與適配邏輯。

| 檔案 | 說明 |
| - | - |
| `AppPackageInfoHelper.cs` | 統一讀取套件識別、版本號、發行者與架構資訊，供設定頁與更新邏輯使用。 |
| `FileHelper.cs` | 檔案讀寫與路徑處理輔助。 |
| `GeoDataStatusTextHelper.cs` | 根據 GeoData 結果產生使用者可讀狀態文字。 |
| `JsonHelper.cs` | JSON 讀寫輔助。 |
| `LiveChartsBootstrapper.cs` | 首頁 LiveCharts 圖表的延遲初始化入口。 |
| `LocalizedStrings.cs` | 本地化字串存取包裝器。 |
| `LogLevelToBrushConverter.cs` | 依照日誌等級映射文字顏色。 |
| `PageMemoryTrimHelper.cs` | 在重頁面離頁與 Shell 凍結後觸發 GC 與工作集收縮。 |
| `ProfileCompatibilityChecker.cs` | 檢查設定檔/訂閱與目前執行環境的相容性。 |
| `ProxyConfigParser.cs` | 解析代理節點設定。 |
| `ProxyGroupParser.cs` | 解析代理群組與成員結構。 |
| `ShareLinkSubscriptionConverter.cs` | 將分享連結訂閱轉成 Mihomo YAML。 |
| `SubscriptionContentNormalizer.cs` | 統一訂閱內容編碼與格式。 |

## Logging/

| 檔案 | 說明 |
| - | - |
| `FileLoggerProvider.cs` | 自訂日誌提供器，把日誌寫入本機檔案。 |

## Models/

`Models/` 定義應用程式狀態物件、頁面顯示模型與執行期資料結構。

| 檔案 | 說明 |
| - | - |
| `AppConfig.cs`、`CloseBehavior.cs` | 應用程式設定與關閉行為定義。 |
| `ConnectionEntry.cs`、`ConnectionsColumnLayout.cs` | 連線頁的資料列模型與欄位版面狀態。 |
| `GeoDataAssetStatus.cs`、`GeoDataFailureKind.cs`、`GeoDataOperationKind.cs`、`GeoDataOperationResult.cs` | GeoData 下載/驗證結果模型。 |
| `HomeChartSample.cs`、`HomeChartState.cs`、`HomeOverviewState.cs` | 首頁圖表採樣點、圖表歷史快取與首頁總覽快照模型。 |
| `LogEntry.cs` | 日誌頁單筆日誌項目。 |
| `MihomoFailureDiagnostic.cs`、`MihomoFailureKind.cs`、`MihomoStatus.cs` | Mihomo 執行狀態與故障診斷資訊。 |
| `MixinSettings.cs`、`PortSettingsDraft.cs`、`ProfileConfigWorkspace.cs`、`ProfileItem.cs` | 設定工作區、Mixin 設定與訂閱資料模型。 |
| `ProxyGroup.cs`、`ProxyGroupLoadResult.cs`、`ProxyGroupMember.cs`、`ProxyNode.cs` | 代理頁使用的節點、群組與載入結果模型。 |
| `PublicNetworkInfo.cs` | 首頁公網網路資訊模型。 |
| `RuntimeRuleItem.cs` | 規則頁展示與啟停用的執行期規則模型。 |
| `UpdateState.cs`、`UpdateStatus.cs` | 更新檢查、下載與安裝鏈的狀態模型。 |

## Serialization/

| 檔案 | 說明 |
| - | - |
| `ClashJsonContext.cs` | `System.Text.Json` 的來源產生器上下文，用於提升 JSON 序列化效能並統一模型註冊。 |

## Services/

### Interfaces/

| 介面 | 說明 |
| - | - |
| `IAppLogService.cs` | 應用日誌收集與查詢介面。 |
| `IAppSettingsService.cs` | 應用設定讀寫介面。 |
| `IConfigService.cs` | 訂閱設定、Mixin、Runtime 與規則開關管理介面。 |
| `IDialogService.cs` | 對話框顯示介面。 |
| `IGeoDataService.cs` | GeoData 準備、刷新與狀態查詢介面。 |
| `IHomeChartStateService.cs` | 首頁圖表歷史快取介面。 |
| `IHomeOverviewSamplerService.cs` | 首頁背景採樣介面，負責持續維護連線、速率、記憶體與圖表歷史。 |
| `IKernelBootstrapService.cs` | Mihomo 核心準備與下載入口。 |
| `IKernelPathService.cs` | 核心路徑解析介面。 |
| `ILoggerService.cs` | 日誌抽象介面。 |
| `IMihomoService.cs` | Mihomo 控制器高階業務介面。 |
| `INavigationService.cs` | 主視窗頁面導覽介面。 |
| `INetworkInfoService.cs` | 公網 IP 與網路歸屬查詢介面。 |
| `IProcessService.cs` | Mihomo 行程啟動、停止與診斷介面。 |
| `IProfileService.cs` | 訂閱資料讀取、儲存、切換介面。 |
| `ISystemProxyService.cs` | Windows 系統代理啟停與同步介面。 |
| `IThemeService.cs` | 主題切換與多視窗主題同步介面。 |
| `ITrayService.cs` | 系統匣介面。 |
| `IUpdateService.cs` | 更新檢查、下載與安裝介面。 |

### Implementations/

| 實作 | 說明 |
| - | - |
| `AppLogService.cs` | 應用程式記憶體日誌與頁面日誌來源實作。 |
| `AppSettingsService.cs` | 本機應用設定讀寫實作。 |
| `DialogService.cs` | 基於 WinUI 對話框的實作。 |
| `GeoDataService.cs` | 呼叫下載腳本並檢查 GeoData 狀態。 |
| `HomeChartStateService.cs` | 保存首頁圖表最近樣本與座標軸上限快取。 |
| `HomeOverviewSamplerService.cs` | 首頁背景採樣實作，持續聚合 Mihomo 連線統計、記憶體與圖表樣本。 |
| `KernelBootstrapService.cs` | 啟動時準備 Mihomo 核心。 |
| `KernelPathService.cs` | 解析目前應使用的 Mihomo 核心路徑。 |
| `MihomoApiClient.cs` | 底層控制器 API 通訊元件。 |
| `MihomoService.cs` | Mihomo 相關高階業務實作，例如代理群組、連線、規則套用與版本讀取。 |
| `NavigationService.cs` | 主視窗各頁面與 ViewModel 的導覽對應。 |
| `NetworkInfoService.cs` | 首頁公網 IP 與網路資訊查詢實作。 |
| `ProcessService.cs` | Mihomo 行程啟動、停止、復用與失敗診斷實作。 |
| `ProfileService.cs` | 訂閱資料載入、儲存、刪除與切換實作。 |
| `SystemProxyService.cs` | 系統代理登錄與同步實作。 |
| `ThemeService.cs` | 主視窗與子視窗主題同步與背板套用實作。 |
| `TrayService.cs` | 系統匣圖示與選單實作。 |
| `UpdateService.cs` | 從 GitHub Release 檢查更新、下載 `.msix` 並呼叫 App Installer。 |

### Implementations/Config/

| 檔案 | 說明 |
| - | - |
| `ConfigService.cs` | 設定工作區、`source.yaml`、`mixin.yaml`、`runtime.yaml` 與規則覆寫檔的核心管理實作。 |
| `ConfigValidator.cs` | 設定合法性檢查。 |
| `ConfigBackupManager.cs` | 設定備份與還原輔助。 |

## ViewModels/

| 檔案 | 說明 |
| - | - |
| `ViewModelBase.cs` | 視圖模型基礎能力。 |
| `MainViewModel.cs` | 主視窗導覽狀態、路由歷史與側邊欄選取邏輯。 |
| `HomeViewModel.cs` | 首頁總覽、圖表、網路資訊、系統資訊展示邏輯。 |
| `ProfilesViewModel.cs` | 訂閱資料管理與切換邏輯。 |
| `ProxiesViewModel.cs` | 代理群組、節點、測速與選擇邏輯。 |
| `ConnectionsViewModel.cs` | 連線頁列表、關閉連線、搜尋與刷新邏輯。 |
| `LogsViewModel.cs` | 日誌頁篩選、複製與主題色邏輯。 |
| `RulesViewModel.cs` | 執行期規則列表、搜尋、啟停與立即生效邏輯。 |
| `SettingsViewModel.cs` | 設定頁、GeoData 更新、連接埠設定、主題、更新與應用設定邏輯。 |

## Views/

### 主視窗與子視窗

| 檔案 | 說明 |
| - | - |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | 應用主視窗的輕量宿主，負責視窗生命週期、最小化凍結、恢復與 Shell 重建。 |
| `MainShellControl.xaml` / `MainShellControl.xaml.cs` | 主視窗導覽外殼，承載 `NavigationView + Frame` 與頂層頁面導覽。 |
| `PortSettingsWindow.xaml` / `PortSettingsWindow.xaml.cs` | 獨立連接埠設定視窗。 |

### Views/Pages/

| 頁面/檔案 | 說明 |
| - | - |
| `HomePage.xaml` / `HomePage.xaml.cs` | 首頁總覽儀表板，顯示連線數、流量、圖表、網路資訊、系統資訊等。 |
| `ProfilesPage.xaml` / `ProfilesPage.xaml.cs` | 訂閱資料頁面。 |
| `ProxiesPage.xaml` / `ProxiesPage.xaml.cs` | 代理群組與節點選擇頁面。 |
| `ConnectionsPage.xaml` / `ConnectionsPage.xaml.cs` | 連線列表、搜尋、關閉連線與連線統計頁面。 |
| `LogsPage.xaml` / `LogsPage.xaml.cs` | 執行日誌檢視頁面。 |
| `RulesPage.xaml` / `RulesPage.xaml.cs` | 執行期規則展示、搜尋與開關頁面。 |
| `SettingsPage.xaml` / `SettingsPage.xaml.cs` | 應用設定、GeoData、核心、更新與執行設定頁面。 |
| `IShellFreezablePage.cs` | 頁面在 Shell 凍結前主動釋放 UI/資料引用的介面。 |

> 目前倉庫沒有獨立的 `Dialogs/` 目錄；對話框能力主要透過 `IDialogService` 與頁面內的 `ContentDialog` 組織。

## i18n/

| 目錄 | 說明 |
| - | - |
| `en-US/` | 英文資源目錄，包含 `Resources.resw` 與英文說明文件。 |
| `zh-Hans/` | 簡體中文資源目錄，包含 `Resources.resw`、README 與簡體中文說明文件。 |
| `zh-Hant/` | 繁體中文資源目錄，包含 `Resources.resw`、README 與繁體中文說明文件。 |

## Build/

| 檔案 | 說明 |
| - | - |
| `DownloadKernel.ps1` | 下載或更新 Mihomo 核心的 PowerShell 腳本。 |
| `DownloadGeoData.ps1` | 下載或刷新 `geoip.metadb`、`geoip.dat`、`geosite.dat` 的 PowerShell 腳本。 |

## 結構補充說明

- 倉庫中的目錄主要描述的是**原始碼結構**與**打包結構**。
- 執行期產生的訂閱資料、核心副本、GeoData、日誌與使用者設定會存放在使用者目錄，不會全部直接提交到倉庫中。
- 頁面層遵循 `Views + ViewModels + Services + Models` 的 MVVM 分層，設定與 Mihomo 執行鏈則集中在 `Services`、`Helpers` 與 `Background` 中實作。
- 目前主視窗採用「輕量宿主 + 可卸載 Shell」結構：最小化時會卸載 WinUI Shell，首頁背景採樣服務仍會維護圖表歷史與總覽資料。
