# BepisDB 插件維護規則

開工前依序閱讀 [架構與不變條件](ARCHITECTURE.md)、[交接與驗證方式](HANDOFF.md)。完整工作區另讀 sibling 路徑 `../KoikatsuSceneGallery/docs/plugin-conventions.md`；本 repo 的建置與測試不得依賴 sibling 原始碼。

- 本 repo 只包含 BepisDB 插件。保留使用者未提交內容；不要改動其他插件、host 或 SDK，除非目前任務明確涵蓋它們。
- `BepisDbPlugin` 只轉接 SDK；composition 與關閉流程在 `BepisDbRuntime`。不要把 HTTP、JSON、磁碟快取或共享請求重新塞回入口。
- 新增 top-level implementation type 時，先將它放入 `ArchitectureTests.EveryProviderType_HasAnExplicitArchitecturalHome` 的責任群組，再檢查對應層的依賴限制；不得用新型別繞過既有 NetArchTest selector。
- 依 [架構](ARCHITECTURE.md) 的擴充位置與依賴方向修改。需改變既有邊界時，先記錄問題、替代方案、相容性影響，再同步更新架構文件及能證明規則有效的測試；不可只為讓 CI 通過而刪除約束或放寬 type selector。
- 修 bug 先建立可重現的失敗測試，記錄原版失敗與新版通過。純搬移不改原有 assertion；保持 provider ID、SDK 能力、設定鍵、快取 JSON 與發布檔名相容。
- SDK 套件參考固定 `SceneGallery.PluginSdk 1.3.0`，其 runtime assembly identity 仍為 `1.0.0.0`；同步檢查專案、架構測試與 `eng/Verify-Plugin.ps1`，不可把套件版號誤當組件版號。
- 共享 fetch 只接受 runtime shutdown token；caller 取消只影響自己的 `WaitAsync`。每筆 fetch 保有 session lease；舊 generation 不得污染新 cookies 或發布新 cache 狀態。
- 設定 `cfClearanceCookie` 持續使用 `CurrentUser` DPAPI，禁止記錄明文、密文或片段。測試不得呼叫外部 BepisDB。
- 驗證一律關閉自動部署：執行 [統一驗證腳本](eng/Verify-Plugin.ps1)，或明確傳 `-p:DeployPluginToApp=false`。禁止為了測試關閉使用者應用程式或覆寫正在載入的 DLL。
- 新增/變更基礎设施不能讓 shipping assembly 多出 runtime dependency。SDK 唯一 runtime 來源是 host；共用實作只能來自已鎖版本、internal 的 source-only 套件。
- 每次行為或 ownership 變更，同步維護本 repo 的 `ARCHITECTURE.md`、`HANDOFF.md` 和實際驗證結果。不得把未執行的 live/manual/CI 驗證寫成通過。

Build 與 Release 都執行同一驗證閘門；發布或跨外部系統操作仍需依任務授權。
