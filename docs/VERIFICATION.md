# 0.2 原型驗證紀錄

日期：2026-09-20。位置：`I:\Codex\工具型\CutMaker`。

## 結果

- Release 建置：0 警告、0 錯誤。
- 核心行為：19／19 通過。
- 真實 MP4／MP3 渲染：29／29 通過。
- WPF 整合：素材匯入、時間軸、編輯、效果、復原／重做、專案讀回、原生預覽、四種視窗尺寸與匯出工具列展開均通過。
- 已目視檢查最終預覽畫面、較窄版面、長名稱及小螢幕匯出狀態的渲染 PNG。

驗證命令：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1`。
本次額外透過 `CUTMAKER_SMOKE_EXTRA_MEDIA` 加入本地 Mozilla CC0 MP4 與 MP3 素材；另產生 PNG、JPEG、PCM WAV、0.7 秒 MP4，共六個匯入素材。

## 驗證內容

- 非破壞性移動、頭尾修剪、來源時間連續、切割、增刪軌道、鎖定、音量及 Fade。
- 實際片段屬性套用、Undo／Redo、剪輯後存檔再開啟；來源檔雜湊及檔案鎖釋放。
- 匯出包含 H.264＋AAC MP4 與 MP3；解碼後檢查影像疊放、修剪取樣位置、黑畫面空白、混音音量、五種淡化曲線及靜音尾段。
- 取消正在運作的編碼器、缺少來源與拒絕覆蓋來源；既有目的檔與原始素材保持完整。
- 預覽檔精確 4.5 秒；Windows 回報的 NaturalDuration 為 4 秒，但可定位到 4.3 秒並播放完整尾端。匯入時長已改用 ffprobe，另驗證 0.7 秒片段不會被略過。
- 預覽過期處理取消、MediaOpened、原生播放器時鐘、播放頭更新及實際畫面。

## 本地證據

- `runtime/verification/smoke/result.txt`
- `runtime/verification/smoke/import-result.txt`
- `runtime/verification/smoke/timeline-result.txt`
- `runtime/verification/smoke/editing-result.txt`
- `runtime/verification/smoke/preview-result.txt`
- `runtime/verification/smoke/workspace-preview.png`
- `runtime/verification/smoke/workspace-small-export.png`
- `runtime/render-checks/render-result.txt`
- `runtime/render-checks/integration.mp4` 與 `integration.mp3`

這些證據與素材留在本地，Git 忽略它們。本次是共用 UI 操作入口、真實編碼／解碼及原生播放測試；尚未完成人工滑鼠手感、長片、大量軌道或不同電腦環境的完整驗收。
