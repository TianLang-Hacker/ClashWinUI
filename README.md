[English]() | [简体中文](./i18n/zh-Hans/README.zh-Hans.md) | [繁體中文](./i18n/zh-Hant/README.zh-Hant.md)

# 🚀 ClashWinUI

ClashWinUI is a modern local network traffic management and routing console built on WinUI 3. This project adopts a strictly decoupled frontend-backend architecture, allowing users to load custom third-party network processing cores (such as Mihomo) to provide a transparent, efficient, and safe network rule configuration experience.

[Core Features](#-core-features) • [Installation Guide](#-installation-guide) • [Localization](#-Localization) • [Developer Guide](#-developer-guide)

---

## ✨ Core Features

* **Network Traffic Monitoring**: Monitor and manage local network requests in real-time, providing an intuitive view of traffic flow.
* **Local Network Flow Distribution**: Support building localized network forwarding rules to precisely control the data paths of various applications.
* **Rule-based Routing Strategy**: A powerful routing engine based on YAML configuration, enabling flexible traffic splitting and network node management.
* **Modern Native Experience**: Fully follows Windows 11 Fluent Design guidelines, with deep support for Mica/Acrylic materials and seamless dark mode switching.
* **Highly Customizable Core Management**: Pure frontend architecture, supporting users to custom import and switch external network core executables, achieving true separation of "configuration" and "UI".

## 📦 Installation Guide

If the `.msix` package downloaded from the Release page cannot be installed directly due to **local self-signed certificate** restrictions, please follow these steps to import the trusted certificate:

1. **Extract Certificate Info**: Right-click the downloaded `.msix` package and select **"Properties"**.
2. **View Signature**: Switch to the **"Digital Signatures"** tab, select the signer (e.g., "Embedded Signature") from the list, and click **"Details"**.
3. **Install Certificate**: In the pop-up window, click **"View Certificate"**, and then click **"Install Certificate"**.
4. **Select Store Location**: In the Certificate Import Wizard, select **"Local Machine"** as the Store Location and click "Next".
5. **Specify Certificate Directory**: Select **"Place all certificates in the following store"**, click "Browse", and choose **"Trusted Root Certification Authorities"**.
6. **Complete Installation**: Click "Next" until "Finish". After the system prompts that the import is successful, double-click the `.msix` file to install the application normally.

## 🌐 Localization

This project currently supports the following interface languages:
* English (en-US)
* Simplified Chinese (zh-Hans)
* Traditional Chinese (zh-Hant)

## 🛠️ Developer Guide

Welcome to participate in the development and maintenance of this project! Please ensure your development environment meets the following basic requirements:

* **IDE**: Visual Studio 2026 or later
* **Required Workloads & SDKs**:
    * .NET Desktop Development
    * WinUI Application Development
    * Windows 11 SDK (10.0.26100.7705)
    * Windows 11 SDK (10.0.22621.0)
    * Universal C Runtime

1. **Clone the repository locally**

```bash
git clone https://github.com/TianLang-Hacker/ClashWinUI.git
```

---

2. **Compile the Project**

You can open the ClashWinUI.slnx solution file with Visual Studio 2026 and press F5 to start compiling, or compile directly using the command line:

X86_64：

```bash
dotnet build -c Release -p:Platform=x64
```

ARM64：

```bash
dotnet build -c Release -p:Platform=arm64
```

Here is the complete [development documentation](./i18n/en-US/ClashWinUIhelp%20en.md), welcome to participate.
