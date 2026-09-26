# BepisDB 維護交接

先讀 [AGENTS](AGENTS.md) 與 [架構](ARCHITECTURE.md)。這份交接用於說明修改入口、已鎖定行為、重現與驗證方式；不是允許任意改寫的待辦清單。

## 本次交付

- SDK 入口縮為轉接，內部新增 runtime、fetch coordinator、session owner、response parser 與 mapper。
- 修正 cookie 更新提前 Dispose 舊 transport、更新後新 caller 誤加入舊 fetch，以及遲到 challenge/validation 污染新 cookie 狀態。
- shared fetch 保留 caller-local cancellation；cookie generation 只允許目前世代發布資料。新增 shutdown admission、單一 deadline、延後 cache disposal，避免在 cache 已 Dispose 後才完成 producer 而漏存結果。
- 2026-09-22 初版以 SDK 套件 `1.0.0` 驗證公開能力/方法、provider ID、檔案命名、設定鍵及 cache JSON；source-only Common/Secrets 升至 `0.2.0`。沒有新增 runtime 組件、發布或推送。
- [ArchitectureTests](SceneGallery.Plugin.BepisDb.Tests/ArchitectureTests.cs) 和 [驗證腳本](eng/Verify-Plugin.ps1) 可在獨立 checkout 執行；Build/Release 在打包前使用相同閘門。

## 建置與驗證

需求：Windows、.NET 10、PowerShell 7，以及 `NuGet.config` 中 SceneGalleryGitHub source 的讀取權限。不要把凭證提交到 repo。

```powershell
rtk proxy pwsh -NoProfile -File ./eng/Verify-Plugin.ps1
```

腳本執行全部 xUnit 測試及 NetArchTest，接著檢查 SDK/source-package metadata、無 sibling ProjectReference、外部 Compile 來源 allowlist、shipping output，並驗證三份交接文件與本地連結。它固定 `DeployPluginToApp=false`，不會覆寫使用者正在載入的插件。

Common `0.2.0` 尚未遠端發布時，完整工作區只能用顯式 local validation config，且指定新的隔離 package cache/output。例：

```powershell
rtk proxy pwsh -NoProfile -File ./eng/Verify-Plugin.ps1 -NuGetConfig ../KoikatsuSceneGallery/eng/PackageValidation/NuGet.config -ArtifactsPath ../validation/bepis-check -PackagesPath ../validation/bepis-check/packages
```

不可把 local feed 加入 committed `NuGet.config`。`--no-build` 的舊 DLL 結果不能證明新版程式通過。若預設 `obj` 因 host 載入/權限而鎖住，使用上述隔離輸出，不要終止使用者應用程式或刪除其資料。

## 回歸證據

[PluginLifecycleTests](SceneGallery.Plugin.BepisDb.Tests/PluginLifecycleTests.cs) 最初六項已在舊實作上先跑出失敗，當時只增加 factory injection seam；再套用修正後通過：

| 既有缺陷 | 失敗優先測試 |
| --- | --- |
| 更新 cookies 時提前 Dispose 使用中的 fetcher | `CookieReplacement_KeepsOldFetcherAliveUntilItsProducerFinishes` |
| 新 cookies 的 caller 加入舊認證請求 | `CookieReplacement_NewCallerDoesNotJoinOldSessionFetch` |
| 遲到 validation 污染 setup state | `CookieReplacement_LateValidationCannotInvalidateNewCookies` |
| 遲到 challenge 污染 setup state | `CookieReplacement_LateChallengeCannotInvalidateNewCookies` |
| 超時 producer 在 cache Dispose 後才完成，資料未落盤 | `Dispose_ProducerOutlivingDeadlineStillPersistsItsFinalResult` |
| Dispose 後仍接受新請求 | `Dispose_RejectsNewProducers` |

同檔補充 preview→save 不重抓、快速多次換 cookies、snapshot 隔離、缺少 clearance 的原行為、無 Title 舊 cache refresh、作者只讀快取與 Dispose 後 mutation 拒絕。原 HTTP/retry、caller cancellation、cache generation/backoff、DPAPI 測試 assertions 保持不變。

交叉審查另外以 `DisposeBeforeInitialize_RejectsStatefulFallbacks` 先重現「未 Initialize 就 Dispose 仍回傳普通 fallback」的缺陷，再補入口狀態防護；並測試正常 pre-init fallback、Dispose 後 Initialize 及重複 Initialize 不得建構新 transport。此項失敗 TRX 保存於工作區 `validation/plugin-refactor-20260922/bepis-entrypoint-red/results`。

2026-09-22 本機驗證：上述六項舊實作回歸及一項入口缺陷均先失敗；隔離還原 Common/Secrets `0.2.0` 後，完整 gate **101/101 tests 通過**，SDK/package 邊界、shipping output 與文件連結也通過。工作區證據放在 `validation/plugin-refactor-20260922/bepis-red/results`、`bepis-green/results` 與最終 `bepis-green/TestResults`，包含 TRX；不要求獨立 checkout 擁有這些機器產物。

2026-09-26 相容性基線更新：依使用者選擇，host 與各插件對齊 SDK 套件 `1.3.0`，但 SDK assembly identity 仍是 `1.0.0.0`。本 repo 同步更新 shipping/test 參考及驗證腳本；公開 BepisDB API、能力、資料格式與 Common/Secrets `0.2.0` 未變。上段 101 項結果屬於 9/22、SDK 套件 `1.0.0` 的歷史結果。新版獨立 gate 在隔離輸出與套件快取 `validation/plugin-refactor-20260926/bepis-sdk13` **101/101 通過**，含架構、套件、shipping output 與文件檢查；TRX 位於該目錄 `TestResults`。沙箱拒讀使用者 Roaming NuGet.Config，因此這次執行把 process `APPDATA` 指向同一隔離目錄，並使用明確 local-feed config；未更改正式套件來源。

2026-09-26 後續架構稽核發現，舊依賴 selector 雖有非空斷言，新增 top-level 型別仍可能不被任何規則選中。因此加入 21 個 provider 型別的完整責任 inventory；未分類新增型別現在會使 gate 失敗，既有負例自測保留。使用 SDK 1.3.0、本機已驗證的隔離套件 cache 與全新 `validation/plugin-refactor-20260926/bepis-audit` 輸出重跑，**102/102 通過**，含 package/shipping/document checks，TRX 位於該目錄 `TestResults`。第一次空 cache 還原因 nuget.org TLS 錯誤失敗；重跑的 NU1900 只表示無法取得遠端套件弱點資料，不代表已完成線上套件安全稽核。

## 尚須人工/外部確認

- 本次未啟動真實 BepisDB、Cloudflare 瀏覽器 cookie setup，也未登入帳號。離線測試證明 parser、傳输分類、cookie 生命週期與資料落盤；不代表外部網站當下可用。
- 未執行遠端 GitHub Actions、發布套件或 release。新版本套件必須先由套件 owner 正式發布，再驗證官方 source 的空 cache restore；本機 local-feed 成功不能替代此項。
- 不支援在同一個已初始化或已 Dispose 的 plugin instance 上重新 Initialize；host 應建立新 instance。對 lifetime 的變更請先看 [架構](ARCHITECTURE.md) 的 owner/producer 規範。

維護人員新增功能時，先找架構表中對應層，補對應離線測試，再跑完整 gate。需要變更 public capability、package 形態或持久化格式時，必須另列相容性決策與遷移驗證，不能當成內部整理一起帶入。

## 2026-09-27 發布候選驗證

遠端既有最新 release/tag 為 `0.0.4`；本次候選版為 `v0.0.5`。以正式注入版本 `0.0.5`、SDK `1.3.0`、Common/Secrets `0.2.0` 及明確的本機 NuGet feed 執行 `eng/Verify-Plugin.ps1`，**102/102 通過**，並通過組件／套件邊界、發佈輸出及文件連結檢查。測試輸出位於此 checkout 的 `bin/release-validation/artifacts`（Git 忽略）。編譯採 `UseSharedCompilation=false` 避免沙箱外的 Roslyn shared compiler 無法寫入隔離輸出；`NuGetAudit=false` 僅用於無法連線 nuget.org 的本機環境，不代表已完成遠端套件安全稽核。這項驗證使用本機套件來源，官方 GitHub Packages 還原及遠端 Release workflow 須在依賴套件發佈後另行確認。
