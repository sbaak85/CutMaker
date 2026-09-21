# 0.8.0 性能與 P1 操作驗證

日期：2026-09-21。位置：`I:\Codex\工具型\CutMaker`。

- 最終 `scripts/verify.ps1` 通過：Release 0 警告／0 錯誤、核心 25／25、渲染／混音 137／137、獨立程序原生 PCM 與 WPF 整合。
- 29.97 fps、22.4 秒影片分成三個畫面區間；尾端 Fade 編輯重算一段、重用兩段。最終執行冷準備 3628 ms、修改後 920 ms；再次呼叫全部命中。這是合成測試素材的單次量測，不是所有素材速度保證。
- 接點兩側影格與連續渲染比較，平均像素差 2.222／255；音訊取樣數一致、RMS 差 0.0003848。既有切割連續性測試比較完整 PCM／MP3／AAC 解碼後取樣，仍全部通過。
- 漸進解碼完成結果與不中斷解碼逐位元組一致；330 秒 MP3 最終起始準備 988 ms，尚未載入的尾段等待真實資料。裝置緩衝不足時時鐘暫停，恢復後沒有補零、漏樣或重複取樣。
- 純音訊播放中音量／MUTE 保留裝置與混音器，240 取樣增益斜坡；準備後外部來源變更會拒絕沿用舊音訊。快取測試含持有中檔案保護、原始素材保護、並行容量預留與釋放；大型代理及背景排程取消均通過。
- 100 次資料更新保留兩條 WPF 軌道容器；局部波形 2048 bins 對準來源 20 秒脈衝。這項容器檢查不等於萬級片段壓力測試。
- 操作檢查 18 項：Fade 擴大熱區、Shift 1/10 與切換連續性、取消、吸附提示、水平捲動、批次 Fade 一次 Undo、偏好與專案視圖記憶、接點精確停止／循環／手動退出。
- 已目視檢查最終預設、窄版長名稱、最小、小螢幕、拖曳提示與吸附線 PNG。工具列換行；窄面板保留捲動。較早完整執行另匯入本地 Mozilla CC0 MP4／MP3；最終執行使用生成的真實影像／音訊素材。

本地證據：`runtime/verification/smoke/performance-result.txt`、`interaction-result.txt`、`workspace-precision-drag.png`、`workspace-snap-guide.png`、`workspace-wave-detail.png`、`runtime/verification/audio-smoke/pcm-device-checks.txt` 及 `runtime/render-checks/render-result.txt`。

操作驗證使用正式手勢共用入口及 WPF 渲染，播放測試使用原生音訊裝置；未進行真人長時間剪輯與不同硬體的手感驗收。即時音量控制目前限純音訊；影片仍需合併預覽，深處冷定位仍需解碼追上，共用容量為保留使用中檔案的軟上限。

# 0.7.1 Fade 即時拖曳驗證

日期：2026-09-21。位置：`I:\Codex\工具型\CutMaker`。

- `scripts/verify.ps1` 全部通過：Release 建置 0 警告／0 錯誤、核心 25／25、渲染／混音整合 116／116，以及 WPF 整合與獨立程序音訊播放。
- Fade-in／Fade-out 在按住期間的兩個中間位置均驗證實際曲線與金色控制點像素；涵蓋正常／30 px 收折軌道、非零時間偏移及縮放。
- 驗證拖曳不改動專案、歷史或預覽版本；Esc／失去擷取還原畫面，放開只增加一次 Undo，連動夥伴效果保持獨立。
- 已查看拖曳中正常／收折畫面，以及預設、窄版、最小與小螢幕版面 PNG。切割沿用既有 Ctrl+B。
- 首次像素檢查失敗來自測試截圖帶入軌道父容器的座標偏移；改用工作區截圖相同的 VisualBrush 渲染方式後通過，未放寬像素條件。

本地證據：`runtime/verification/smoke/fade-drag-result.txt`、`workspace-fade-*-dragging.png`、`runtime/verification/audio-smoke/result.txt` 與 `runtime/render-checks/render-result.txt`。額外匯入本地 Mozilla CC0 MP4／MP3 素材。

Fade 手勢驗證呼叫正式滑鼠處理器共用的候選更新入口、放開／取消入口並檢查 WPF 渲染像素；尚未做真人滑鼠手感驗收。

# 0.2 原型驗證紀錄（歷史）

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
