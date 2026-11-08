# AI 帳號管理工具

我訂了多個 AI 帳號，為了更換帳號方便和控管額度，會需要一些第三方工具。

雖然很多人有寫相應的開源工具，但我需要的功能散布在不同工具裡，而我 Claude Code 和 Codex 都有在使用。別人寫的工具有些對我來說有點不足，有些則是有很多功能用不到，反而讓操作過於複雜。

後來想想，都 AI 時代了，自己需要的工具，讓 AI 幫我產生一個客製化版本就好。

## 專案介紹

Windows x64 桌面應用程式，以 WPF 承載 Blazor Hybrid 與 MudBlazor。提供 Claude 與 Codex 的帳號快速切換、額度顯示，以及 Claude 切換帳號時的 Code Session 索引同步。

> [!WARNING]
> 本專案為個人專案，功能依我自己的使用情境設計。帳號切換與額度查詢依賴 Claude Desktop、Codex Desktop 的本機檔案格式與非公開 API，官方更新後可能失效。

## 功能

- Codex OAuth profile 使用各自固定的 CODEX_HOME 與 Windows keyring；帳號身分確認後才保存 profile。
- 切換 Codex Desktop 帳號時，先正常關閉 Desktop，再替換認證快照並重新啟動；失敗時回復原帳號。
- 保存與切換 Claude Desktop 帳號。辨識 MSIX 與直接安裝版，以 profile API 核對身分；保存前要求 Desktop 已正常結束。
- Claude 切換帳號時，將來源帳號的 Code Session 索引覆蓋到目標帳號；對話 transcript 為同機共用檔案，不複製或改寫。
- 顯示 Claude 與 Codex 各帳號的額度、重置時間及最後成功更新時間；無成功資料時顯示未知，不填入 0%。
- 支援手動刷新及 1、5、15、30 分鐘排程，錯誤會保留最後一份完整快照並依退避時間重試。
- 額度偏低時發出系統匣通知，同一個額度視窗以分鐘精度去重，每輪只發一則。
- 重複啟動時喚醒既有視窗，不建立第二份背景服務；既有程序無回應時提示使用者。
- 設定保存於目前 Windows 使用者的本機資料目錄；主題可選跟隨系統、淺色或深色。
- 使用者提供 HTTPS Velopack feed 後可檢查、下載與套用更新；更新套用會等待帳號操作結束。

## 技術與需求

- Windows x64、.NET SDK 10.0.403；global.json 固定 SDK 版本。
- WPF + Microsoft.NET.Sdk.Razor Blazor Hybrid、WebView2 Evergreen Runtime、MudBlazor。
- Codex app-server rust-v0.162.1，封裝與執行前都核對官方 release binary hash。
- Velopack 1.2.161；NuGet 套件版本集中於 Directory.Packages.props。
- NUnit 5 測試專案；元件互動測試使用 bUnit，端對端 UI 測試使用 FlaUI（UIA3）。

安裝器會處理 Microsoft Edge WebView2 Evergreen Runtime，程式啟動時仍會檢查 Runtime。產品使用目前 Windows 使用者的 DPAPI 保護本機 identity key；Codex OAuth token 由隔離 profile 的 Codex app-server/keyring 管理，不寫入產品 JSON 檔。

## 安裝

從 [GitHub Releases](https://github.com/CloudyWing/AiAccountManager/releases/latest) 下載 `ai-account-manager-win-Setup.exe` 執行即可。安裝目錄為 `%LOCALAPPDATA%\ai-account-manager\current`，使用者資料獨立保存於 `%LOCALAPPDATA%\AiAccountManager\data`，解除安裝與升級都不影響該資料目錄。

也可以用 WinGet 安裝：

```powershell
winget install CloudyWing.AiAccountManager
```

每次發佈正式版時，release workflow 會自動把 manifest 以 PR 提交至 `microsoft/winget-pkgs`。PR 經 winget-pkgs 審核合併後，該版本才能用上述命令安裝；審核期間請改用 GitHub Releases 的安裝檔。以本機 manifest 安裝的步驟見 [docs/packaging.md](docs/packaging.md)。

## 開發

在 Windows PowerShell 執行：

```powershell
$env:NUGET_PACKAGES = Join-Path $env:LOCALAPPDATA 'AiAccountManager\NuGet'
$env:DOTNET_CLI_HOME = Join-Path (Get-Location).Path '.local\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

dotnet restore .\AiAccountManager.slnx
dotnet build .\AiAccountManager.slnx -c Release --no-restore
dotnet test .\tests\AiAccountManager.Tests\AiAccountManager.Tests.csproj -c Release --no-build --no-restore
dotnet test .\tests\AiAccountManager.Components.Tests\AiAccountManager.Components.Tests.csproj -c Release --no-build --no-restore
```

測試專案分工：

- `AiAccountManager.Tests`：Application、Windows 介面卡與頁面靜態渲染測試。
- `AiAccountManager.Components.Tests`：以 bUnit 驗證 Razor 元件的點擊、輸入與 callback 行為。
- `AiAccountManager.UiTests`：以 FlaUI 啟動實際的桌面程式，驗證主視窗、WebView2 內容與頁面導覽；測試分類為 `UI`。

UI 測試需要互動式 Windows 桌面與 WebView2 Runtime，並會啟動與測試相同組態建置的 Desktop 執行檔。程式以 `--data-root` 指向暫存目錄，不讀寫 `%LOCALAPPDATA%\AiAccountManager\data`；但仍會讀取本機 Claude Desktop 與 Codex 的安裝與登入狀態。程式採單一執行個體，執行 UI 測試前請先結束正在執行的 AI 帳號管理工具，否則測試會略過。

```powershell
dotnet test .\tests\AiAccountManager.UiTests\AiAccountManager.UiTests.csproj -c Release --no-build --no-restore
```

若要測試其他位置的執行檔，可設定 `AIACCOUNTMANAGER_DESKTOP_EXE` 環境變數指定完整路徑。執行整個方案的測試時，可用 `--filter "TestCategory!=UI"` 排除 UI 測試。

所有驗證與 packaging 命令需在 Windows 執行。細節見 [docs/packaging.md](docs/packaging.md)。

## 資料位置

- 設定、catalog、quota 快照與 DPAPI-protected identity key：%LOCALAPPDATA%\AiAccountManager\data
- Codex profile home：%LOCALAPPDATA%\AiAccountManager\data\codex-profiles\<guid>\CODEX_HOME
- Codex Desktop 登入快照與切換交易紀錄（DPAPI 保護）：%LOCALAPPDATA%\AiAccountManager\data\codex-desktop
- Velopack 安裝檔案：%LOCALAPPDATA%\ai-account-manager\current

Codex Desktop 的 `%USERPROFILE%\.codex\auth.json` 只在使用者保存登入或執行帳號切換時讀寫。切換會先正常關閉 Codex Desktop，寫入目標帳號的登入快照並確認身分後再重新開啟；失敗時還原原本的登入。CODEX_HOME 路徑以 profile GUID 固定，刪除 profile 會先清除該隔離 home 的 keyring entry。

## 本機封裝

package-windows.ps1 需要本機的 app-server ZIP cache 與 Velopack CLI。Velopack CLI 以 dotnet local tool 宣告於 .config\dotnet-tools.json，腳本會先執行 `dotnet tool restore` 再呼叫 `dotnet tool run vpk`。app-server ZIP 預設讀取 repository .local 目錄，也可提供可攜參數：

```powershell
pwsh -NoProfile -File .\scripts\package-windows.ps1 -Version 0.3.0 -AppServerZipPath .\.local\cache\codex-app-server\rust-v0.162.1\codex-app-server-x86_64-pc-windows-msvc.exe.zip
```

需要改用其他位置的 vpk.exe 時，以 `-VpkToolPath` 指定。

輸出包含 `artifacts\releases\ai-account-manager-win-Setup.exe`、Velopack .nupkg、releases.win.json、第三方 notices 與 SHA-256 manifest。驗證通過後會替換 `artifacts\releases` 與 `outputs\first-release`，不使用版本子目錄。封裝腳本不執行安裝器，也不發布更新 feed。

## 授權

本專案以 [MIT License](LICENSE.md) 授權。

安裝套件內附的 OpenAI Codex app-server 以 Apache License 2.0 授權，未經修改直接散布，來源版本、SHA-256 與授權全文見 [packaging/THIRD-PARTY-NOTICES.md](packaging/THIRD-PARTY-NOTICES.md)。
