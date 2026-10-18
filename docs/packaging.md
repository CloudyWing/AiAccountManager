# Windows 建置與封裝

## 工具與版本

- Windows x64
- .NET SDK 10.0.401（global.json）
- WebView2 Evergreen Runtime（產品執行環境）
- Velopack CLI（vpk）1.2.161，以 dotnet local tool 宣告於 .config\dotnet-tools.json
- Codex app-server rust-v0.162.1 官方 Windows x64 ZIP

封裝前需取得已核對來源的 app-server ZIP。Velopack CLI 由封裝腳本執行 `dotnet tool restore` 取得。不要將認證資料或本機 feed 憑證放入 repository。

## 建置與測試

在 PowerShell 設定 dotnet 環境，並在同一個 shell 執行：

    $env:NUGET_PACKAGES = Join-Path $env:LOCALAPPDATA 'AiAccountManager\NuGet'
    $env:DOTNET_CLI_HOME = Join-Path (Get-Location).Path '.local\dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'

    dotnet restore .\AiAccountManager.slnx
    dotnet build .\AiAccountManager.slnx -c Release --no-restore
    dotnet test .\tests\AiAccountManager.Tests\AiAccountManager.Tests.csproj -c Release --no-build --no-restore

Release build 使用專案設定的 win-x64 與 self-contained 屬性，供 packaging script 的 dotnet publish --no-build --no-restore 重用。

## 封裝

預設 cache 相對路徑：

- .local\cache\codex-app-server\rust-v0.162.1\codex-app-server-x86_64-pc-windows-msvc.exe.zip

未提供 `-VpkToolPath` 時，腳本執行 `dotnet tool restore --tool-manifest .config\dotnet-tools.json`，再以 `dotnet tool run vpk` 封裝。若 cache 位於其他位置，或要改用指定的 vpk.exe，透過參數指定，不修改腳本中的機器路徑：

    pwsh -NoProfile -File .\scripts\package-windows.ps1 -Version 0.1.0 -AppServerZipPath .\.local\cache\codex-app-server\rust-v0.162.1\codex-app-server-x86_64-pc-windows-msvc.exe.zip

在隔離 worktree 執行時也可將來源 cache/tool 路徑傳入：

    pwsh -NoProfile -File .\scripts\package-windows.ps1 -Version 0.1.0 -AppServerZipPath <verified-app-server-zip> -VpkToolPath <vpk-1.2.161-executable> -NugetPackagesPath <nuget-package-cache>

封裝步驟：

1. 驗證 app-server ZIP 的 SHA-256 與唯一預期 ZIP entry。驗證完成前不建立封裝 stage，也不變更既有 release 或 first-release 產物。
2. 在 `artifacts\.package-stage-<id>` 建立本次執行專屬 publish 與 VPK 輸出目錄。執行 dotnet publish，使用 Release build 既有資產且不再建置或還原。
3. 解壓 app-server 至本次 stage 的 `publish\tools\codex-app-server\0.162.1`，核對 executable hash 並寫入版本 manifest。
4. 將 packaging\THIRD-PARTY-NOTICES.md 納入 stage publish 目錄與產品 package。
5. 執行 `vpk pack`（未指定 `-VpkToolPath` 時為 `dotnet tool run vpk -- pack`，`--` 讓 `dotnet tool run` 把後續參數全部轉給 vpk），明確指定 `win-x64`，不使用簽章參數。腳本將 publish 與 VPK 輸出都指向本次 stage。

    ```powershell
    dotnet tool run vpk -- pack --packId ai-account-manager --packVersion 0.1.0 --runtime win-x64 --packDir .\artifacts\.package-stage-<id>\publish --mainExe CloudyWing.AiAccountManager.Desktop.exe --outputDir .\artifacts\.package-stage-<id>\releases --instReadme .\packaging\THIRD-PARTY-NOTICES.md
    ```
6. 檢查唯一的 `*-Setup.exe`（本次產物為 `ai-account-manager-win-Setup.exe`）、至少一個 .nupkg、releases.win.json 的版本與 package 參照，並確認 package 內含 app-server binary 及 third-party notices。
7. 產生 release-artifacts.json，記錄每個 VPK 輸出檔案的大小與 SHA-256。腳本核對 release stage 與 `outputs\.first-release-stage-<id>` 的完整檔案清單及 SHA-256，全部通過後才替換 `artifacts\releases` 與 `outputs\first-release`。若驗證失敗，既有兩個目錄維持原樣；若替換過程失敗，腳本會嘗試還原備份目錄，還原失敗時保留備份供復原。

腳本會把 dotnet/vpk 的 command、UTC 起訖時間、exit code、stdout 與 stderr 分別記錄於 artifacts\package-logs。它不執行 Setup.exe，也不連線 feed 或發布套件。

## 更新 feed

第一版 UpdateFeedUrl 預設為 null。未設定時，應用程式不建立 Velopack UpdateManager，不送出檢查或下載 request。使用者保存絕對 HTTPS feed 後，設定頁才開放檢查、下載及套用。

更新套用會暫停 quota scheduler、拒絕新的 profile/quota 工作，等待目前操作完成。等待超過 30 秒會取消套用並恢復 scheduler。產品安裝目錄為 %LOCALAPPDATA%\ai-account-manager\current；使用者資料獨立保存於 %LOCALAPPDATA%\AiAccountManager\data。
