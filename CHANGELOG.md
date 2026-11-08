# Changelog

## v0.3.0 (2026-11-09)

### New Features

- 設定頁的低額度通知區新增「傳送測試通知」按鈕。測試氣泡和正式通知格式相同，內容改用範例值並標示為測試。低額度通知關閉時仍可傳送，點擊氣泡不切換頁面，也不影響正式通知的去重紀錄。
- 發佈正式版時，release workflow 會自動把 WinGet manifest 以 PR 提交至 `microsoft/winget-pkgs`，版號含 `-` 的預發布版本不提交。PR 審核合併後，可用 `winget install CloudyWing.AiAccountManager` 安裝。

### Changes

- 6 個 Dialog 的標題移到標題區，下方加上分隔線，標題與內容的間距加大，不會再被看成內容中的章節標題。
- Codex 帳號卡的重置券明細改為縮排清單：名稱靠左、到期日靠右，即將到期的項目以警示色標示，明細與「使用重置券」按鈕之間加大間距。明細標題改用 chevron 圖示，滑鼠點擊後不再出現白色外框，鍵盤操作時仍有 focus 指示。

### Bug Fixes

- 修正解除安裝後安裝目錄殘留，導致無法重新安裝的問題。原因是從本工具開啟的 Claude Desktop 與瀏覽器會繼承安裝目錄作為工作目錄，使安裝目錄被鎖住。現在程式啟動時若工作目錄位於安裝目錄內，會改用使用者設定檔目錄。
- 修正帳號卡展開按鈕的 `aria-expanded` 在收合時缺漏、展開時為空值的問題。

### Build & CI

- 新增 bUnit 元件測試與 FlaUI UI 測試專案。
- 新增以 `CloudyWing/devops-templates` 執行的 CI，並排除需要互動式桌面的 UI 測試。
- release workflow 改由共用流程建立 GitHub Release 並上傳附件。
- 新增 Dependabot 設定。

### Documentation

- README 與 docs/packaging.md 補充 WinGet 安裝與自動提交說明，封裝範例版號更新為 0.3.0。
- docs/packaging.md 新增解除安裝後重新安裝仍顯示「已安裝」的疑難排解。

## v0.2.1 (2026-11-05)

### Bug Fixes

- 修正 Codex 與 Claude 頁面在載入或畫面更新途中被釋放（例如切換到其他頁面）時，程式可能因未處理例外而整個結束的問題。頁面改為在初始化時保存生命週期取消權杖，並在每次等待後確認頁面仍存在；Claude 頁面釋放後也不再誤設載入失敗訊息。
- 修正淺色主題下 AppBar 標題與選單圖示為白字白底而看不見的問題。

### Documentation

- README 與 docs/packaging.md 的封裝範例版號更新為 0.2.1。

## v0.2.0 (2026-11-04)

### New Features

- 安裝器改由 Velopack 處理 Microsoft Edge WebView2 Evergreen Runtime，程式啟動時仍保留 Runtime 檢查與失敗原因提示。
- 重複啟動時喚醒並前景化既有視窗，不建立第二份背景服務；既有程序無回應時顯示提示後結束。
- 新增 WinGet manifest 與產生器，含中英文 locale、安裝器資訊與一致性測試。manifest 未提交至 microsoft/winget-pkgs，需以本機 manifest 安裝。

### Changes

- 使用者可見的顯示名稱統一由 AppIdentity 常數提供，涵蓋視窗標題、開始選單、已安裝應用程式清單、系統匣與通知來源。Package ID 與安裝路徑不變。
- 額度偏低通知改以分鐘精度正規化去重，同一個額度視窗每輪只發一則；決策日誌不含帳號識別、別名或 email。

### Bug Fixes

- 修正主視窗內容區可能完全空白且宿主程序持續高 CPU 的問題。額度讀取不再等待 Claude 作業閘門，背景作業停在網路呼叫時，儀表板仍可完成首次轉譯；帳號頁讀取只在可觀察狀態實際改變時發出狀態變更事件，避免自我觸發的重複讀取迴圈。
- 修正並行作業可能互相覆寫額度狀態的問題。額度讀取改以寫入版本與讀取世代保護，寫入作業的結果不被較舊的讀取覆蓋，兩個並行讀取逆序完成時較舊者不覆寫較新者。
- 修正 Claude Desktop 啟動沒有回應時帳號切換無限等待的問題。啟動等待上限為 60 秒；逾時後啟動仍在進行時，狀態顯示未知，並拒絕再次啟動與帳號切換寫入，直到該次啟動結束。

### Documentation

- README 新增安裝章節，說明 GitHub Releases 安裝與 WinGet manifest 的實際可用範圍，功能清單補上低額度通知與重複啟動喚醒。
- README 與 docs/packaging.md 的封裝範例版號更新為 0.2.0。

## v0.1.0 (2026-11-01)

### New Features

- 新增 Codex 帳號保存、額度查詢與排程刷新，以及 Codex Desktop 帳號切換，切換失敗時回復原帳號。
- 新增 Claude Desktop 帳號保存、切換與額度查詢，支援 MSIX 與直接安裝版，切換時同步 Code Session 索引。
- 新增儀表板，顯示 Claude 與 Codex 各帳號的額度、重置時間與更新時間，背景刷新後自動更新。
- 新增 Codex 重置券的數量與到期顯示、確認後使用，以及到期前自動使用的實驗性功能。
- 新增 Claude 額外重置的剩餘次數與期限顯示。
- 新增 Codex 重置消息卡，顯示預定與最近一次的全體重置，資料來源為第三方網站 codex-resets.com。
- 新增關閉視窗時縮到系統匣，以及開機自動啟動。
- 新增額度偏低通知，以及自動使用重置券後的系統匣通知。
- 新增儀表板的建議切換帳號，在目前帳號額度偏低時列出候選帳號並可直接前往切換。
- 新增 Velopack 自動更新，未設定更新來源時使用 GitHub Releases。
- 新增跟隨系統、淺色與深色主題設定。
