# ClashWinUI 專案結構說明

## 根目錄檔案

|檔案/資料夾|說明|
|-|-|
|App.xaml|應用程式的 XAML 進入點，定義全域資源（如樣式、主題）。|
|App.xaml.cs|應用程式程式碼後置：設定依賴注入容器、設定全域例外處理、初始化日誌、偵測單一實例、啟動系統匣服務。|
|Package.appxmanifest|WinUI 3 應用程式資訊清單檔案，定義套件名稱、功能（如網絡權限）、圖示、啟動視窗等。|
|Assets/|存放應用程式所需的靜態資源檔案。|

### Assets/

|檔案|說明|
|-|-|
|icon.png|應用程式圖示（系統匣、工作列等使用）。|
|(其他圖片/字型)|如背景圖、按鈕圖示、自訂字型等。|

## Kernels/

存放 Clash / mihomo 核心執行檔。此資料夾不加入版本控制，由建置指令碼自動下載。

|檔案|說明|
|-|-|
|.gitkeep|僅用於保留空目錄，使 Git 能追蹤此資料夾。|

## Common/

存放全域常數和公用輔助類別。

|檔案|說明|
|-|-|
|AppConstants.cs|靜態類別，定義 mihomo 預設連接埠、API 路徑、版本號、核心下載網址等常數。|
|ObservableExtensions.cs|針對 System.Reactive 或 IAsyncEnumerable 的擴充方法，簡化響應式編程。|

## Views/

存放所有 XAML 使用者介面檔案，包括主視窗、頁面和對話方塊。

|檔案/資料夾|說明|
|-|-|
|MainWindow.xaml|應用程式主視窗，包含導覽框架（NavigationView）等版面配置元素。|
|MainWindow.xaml.cs|主視窗程式碼後置（通常很精簡，僅初始化元件和綁定資料內容）。|
|Pages/|存放應用程式的主要頁面（每個頁面一個 XAML + 程式碼後置）。|
|├─ HomePage.xaml|首頁：顯示代理節點、流量圖表、開關等。|
|├─ HomePage.xaml.cs|首頁UI實作：顯示代理節點、流量圖表、開關等的補充UI互動程式碼，業務邏輯等放到ViewModels/HomeViewModel.cs|
|├─ SettingsPage.xaml|設定頁：設定應用程式選項、核心參數、主題等。|
|├─ SettingsPage.xaml.cs|設定頁UI實作：設定應用程式選項、核心參數、主題等的補充UI互動程式碼，業務邏輯等放到/ViewModels/SettingsViewModel.cs|
|Dialogs/|存放自訂對話方塊（XAML + ViewModel 成對出現）。|
|├─ ExternalOpenDialog.xaml|範例：外部連結開啟確認對話方塊。|
|├─ ExternalOpenDialogViewModel.cs|該對話方塊的視圖模型。|

## ViewModels/

實作 MVVM 的視圖模型層，負責業務邏輯和狀態管理。

|檔案|說明|
|-|-|
|ViewModelBase.cs|視圖模型基底類別，通常繼承自 ObservableRecipient 或 ObservableObject（CommunityToolkit.Mvvm），實作屬性更改通知、訊息收發等。|
|MainViewModel.cs|主視窗的視圖模型，管理全域狀態（如目前頁面、系統匣互動命令）。|
|HomeViewModel.cs|首頁視圖模型：處理節點清單、流量數據、代理開關等。|
|SettingsViewModel.cs|設定頁視圖模型：載入/儲存設定、主題切換、核心更新觸發等。|

## Models/

定義資料實體和狀態物件。

|檔案|說明|
|-|-|
|MihomoStatus.cs|表示核心執行狀態（是否執行、目前模式、記憶體/CPU 使用等）。|
|ProxyNode.cs|代理節點資訊（名稱、類型、延遲、流量統計等）。|
|AppConfig.cs|應用程式設定（使用者設定、UI 偏好、核心路徑等）。|

## Services/

核心服務層，封裝所有與外部互動和業務操作。

### Interfaces/

定義服務介面，便於依賴注入和單元測試。

|介面|說明|
|-|-|
|IMihomoService.cs|進階業務介面：切換節點、更新規則、獲取狀態等，依賴底層 API 用戶端。|
|IConfigService.cs|設定管理：讀取/寫入設定檔、驗證、備份還原。|
|IProcessService.cs|處理程序管理：啟動/停止核心、提權請求、處理程序監控。|
|INavigationService.cs|頁面導覽服務，供 ViewModel 呼叫切換頁面。|
|IDialogService.cs|對話方塊服務，顯示訊息方塊、自訂對話方塊。|
|IUpdateService.cs|核心更新服務：檢查新版本、下載、替換核心。|
|ITrayService.cs|系統匣服務：初始化系統匣圖示、顯示選單、回應點擊。|
|ILoggerService.cs|（選擇性）自訂日誌介面，如果未使用 Microsoft.Extensions.Logging 的內建介面。|

### Implementations/

介面的具體實作。

|實作類別|說明|
|-|-|
|MihomoService.cs|實作 IMihomoService，呼叫 MihomoApiClient 完成業務邏輯，並處理狀態更新。|
|MihomoApiClient.cs|底層 API 通訊：封裝 HTTP 請求和 WebSocket 連線，處理 Token 驗證、心跳、重新連線。|
|ProcessService.cs|實作 IProcessService：透過 System.Diagnostics.Process 啟動核心，支援提權（runas），監聽處理程序退出事件，提供健康檢查方法。|
|NavigationService.cs|實作 INavigationService：基於 Frame 的導覽，支援參數傳遞。|
|DialogService.cs|實作 IDialogService：使用 ContentDialog 顯示對話方塊，支援非同步等待結果。|
|UpdateService.cs|實作 IUpdateService：從 GitHub Releases 等來源檢查版本，下載檔案並驗證雜湊值。|
|TrayService.cs|實作 ITrayService：使用 H.NotifyIcon 函式庫建立系統匣圖示和右鍵選單，處理選單點擊事件。|

### Config/

設定管理相關實作，按模組拆分。

|檔案|說明|
|-|-|
|ConfigService.cs|主設定服務，組合驗證器和備份管理員。|
|ConfigValidator.cs|設定驗證邏輯（YAML 格式、必要欄位等）。|
|ConfigBackupManager.cs|自動備份與還原設定，防止設定損壞。|

## Background/

存放背景執行的任務和監控元件，通常執行於非 UI 執行緒。

|檔案|說明|
|-|-|
|ProcessMonitor.cs|核心處理程序監控：監視處理程序退出事件，根據策略自動重新啟動，並通知相關服務。|
|MihomoEventSubscriber.cs|WebSocket 事件訂閱：連接 mihomo 的 /traffic、/logs 等端點，將推播的資料轉換為可觀察的串流，供 ViewModel 訂閱。|
|HealthChecker.cs|健康檢查：定期向 mihomo API 發送請求，檢查核心是否回應，異常時觸發通知或自動還原。|

## Exceptions/

自訂例外與全域例外處理邏輯。

|檔案|說明|
|-|-|
|GlobalExceptionHandler.cs|全域未處理例外捕獲：註冊到 AppDomain.UnhandledException 和 DispatcherUnhandledException，記錄日誌並顯示友善提示。|
|KernelException.cs|核心相關自訂例外（如啟動失敗、API 逾時等），便於區分錯誤類型。|

## Logging/

日誌記錄相關元件（如果使用自訂日誌提供者）。

|檔案|說明|
|-|-|
|FileLoggerProvider.cs|自訂檔案日誌提供者，實作 ILoggerProvider，將日誌寫入本機檔案。|

## Helpers/

通用輔助工具類別。

|檔案|說明|
|-|-|
|FileHelper.cs|檔案操作輔助：讀寫檔案、複製、刪除、路徑處理等。|
|JsonHelper.cs|JSON 序列化 / 反序列化封裝（基於 System.Text.Json 或 Newtonsoft.Json）。|
|LocalizedStrings.cs|國際化輔助：提供資源字串的綁定存取，支援動態語言切換。|
|（其他）|可能包含從原 Infrastructure 遷移過來的自訂輔助類別。|

## i18n/

多語言資源資料夾。

|資料夾|說明|
|-|-|
|en-US/|美國英語資源：包含 Resources.resw 檔案，定義所有英文文字。|
|zh-Hans/|簡體中文資源。|
|zh-Hant/|繁體中文資源。|

## Build/

存放與建置相關的指令碼和設定檔。

|檔案|說明|
|-|-|
|DownloadKernel.ps1|PowerShell 指令碼，在編譯前自動下載指定版本的 mihomo 核心，支援版本鎖定和雜湊驗證。|
|（後續的其他建置相關檔案）|建置後處理指令碼、程式碼簽署工具等。|