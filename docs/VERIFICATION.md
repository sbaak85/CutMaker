# 音檔時間比例與播放框（2026-09-22）

- 路徑：`C:\Users\sbaak.fang\ChatGPT\CutMaker`；沿用父目錄共用 SDK／FFmpeg，未安裝新工具。
- Release 建置 0 警告／0 錯誤；核心 26/26；完整渲染／混音 150/150。最後介面與邊界修正後，重新建置、變速專項 13/13，以及獨立資料目錄 WPF 整合均通過。
- 10 秒 440 Hz 音訊，80%／120% 分別產生精確 8／12 秒；維持 440 Hz 音高。預覽與 WAV 匯出取樣一致（量化容許 2 LSB），切割前後逐取樣完全一致；頭部裁切仍映射至相同變速來源。來源 SHA-256 不變，重複比例重用快取，取消不發布可播放專案。
- 模型檢查包含比例、來源範圍、Fade 時間縮放、裁切／切割、預設 100% 的舊資料相容性、範圍存回資料與無效數值拒絕。
- WPF／原生 PCM 檢查包含比例輸入與 Undo／Redo、播放框幾何、邊界調整／取消／Undo、框外按播放自動回框頭、精確框尾取樣停止、循環、接點試聽遵守框限、停用後框外播放及移除隱藏。
- 已目視最終時間比例欄位與播放框 PNG，並檢查收折軌道小視窗。預設／窄版／最小／小螢幕版面測試通過；窄視窗隱藏重複的「時間軸」標題以保留工具按鈕與軌道空間。
- 完整檢查記錄：`runtime/time-ratio-verify.log`；最終建置：`runtime/time-ratio-final-build.log`；最終 WPF：`runtime/time-ratio-final/smoke/`；音訊專項：`runtime/tempo-final/`。生成物不納入 Git。
- 邊界拖曳以共用事件處理入口驗證，聲音以原生裝置與取樣比較驗證；未宣稱真人滑鼠手感或人工聽感驗收。播放框只限制預覽，匯出區間獨立；變速目前只適用於獨立音檔，支援 25%–400%。

## C／WASD 快捷鍵與吸附提示

- C 切換吸附（按住不連續切換）；W／S 定位主選素材頭尾；A／D 逐幀，保留方向鍵及 Ctrl 組合鍵。文字欄位沿用輸入焦點隔離。
- 提示位於播放頭右側，靠右邊界時限制在可視區；完整停留 1 秒，DoubleAnimation 於接著 0.2 秒由 1 淡至 0。連續切換以新提示取代並重新計時。
- Release 0 警告／0 錯誤，核心 25/25、原生音訊檢查通過。新增 W/S、A/D、提示停留／重啟／消失檢查通過，已目視 workspace-snap-toggle-hint.png。
- 首輪既有尺規後 Space 播放檢查失敗；未改動該播放處理，獨立資料目錄的最終 WPF 全整合重跑通過（包含該檢查）。保留此環境波動記錄，不宣稱真人操作驗收。
- 最終 WPF 證據：runtime/hotkey-verification/smoke/，為 Git 忽略的本機生成物。
## 選取醒目度與跨軌殘留修正

- Ctrl 框選改為取代舊選取；Ctrl+Shift 框選追加。單擊放開已選素材回到單選（既有連動夥伴仍保留），拖曳仍支援群組移動，Esc 清除選取。
- 多選素材採亮金底與 1.5 DIP 金框，主選採更亮金底與 2 DIP 淡金框；未選素材保持深色。
- 新增單軌框選清除其他軌舊選取、明確追加、單擊退出多選三項回歸，最終 WPF 整合通過；已目視 workspace-selection-primary.png。仍非真人滑鼠手感驗收。
- 新增回歸證據位於 runtime/selection-verification/smoke，Git 忽略。
# 時間軸視覺與操作更新（2026-09-21，此電腦）

- 位置：C:\Users\sbaak.fang\ChatGPT\CutMaker；使用父目錄共用 SDK 10.0.401 與 FFmpeg 9.0.2。
- scripts/verify.ps1 最終完整通過：Release 0 警告／0 錯誤、核心 25/25、渲染／混音 137/137、獨立程序原生 PCM 與 WPF 整合。
- 新增操作檢查通過：左右括號修剪頭尾而不切割、一次 Undo、播放頭在外拒絕、B 切割與 Ctrl+B 停用、42 DIP 尺規、空白拖曳定位保留選取與歷史、Ctrl 空白手勢啟動框選。操作總計 26 項通過。
- 已目視預設／收折、小螢幕、最小視窗、窄版長名稱及局部金色波形渲染圖。小視窗以減少時間軸面板留白容納尺規高度，保留素材庫與軌道操作空間。
- 深金片段直角細框，頭尾各 30% 漸層、左上與右下邊框高光；概覽與局部波形快取版本更新以避免沿用舊色。
- 離屏測試遇到系統滑鼠擷取立即遺失，改用可選 captureMouse=false 測試共用手勢入口；正式滑鼠操作仍預設擷取。Fade 像素、候選、提交、Undo、Esc 與失去擷取處理檢查保留並通過。此結果不代表真人滑鼠／鍵盤手感驗收。
- 本次證據：runtime/verification/smoke/interaction-result.txt、fade-drag-result.txt、result.txt、workspace-*.png 與 runtime/render-checks/；生成物不納入 Git。
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
