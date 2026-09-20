# CutMaker

Windows 本機影片／音訊剪輯器。**0.2 可操作原型：匯入、剪輯、預覽、儲存與 MP4／MP3 匯出。**

## 開始使用

在 `I:\Codex\工具型\CutMaker\` 雙擊 `啟動CutMaker.cmd`。

1. 把 MP4、MP3、WAV、PNG 或 JPG／JPEG 從檔案總管拖進素材庫，也可按「匯入素材」。
2. 把素材名稱拖到時間軸：影片／圖片放影片軌，音訊放音訊軌；可自由新增軌道。
3. 拖曳片段中央移動，拖曳左右邊緣修剪。點選片段後，拖上緣金色方塊調整淡入／淡出長度。
4. 點時間尺定位播放頭，選取片段後按「切割」或 Ctrl+B。Delete 移除；Ctrl+Z 復原，Ctrl+Y 重做。
5. 右側可精確輸入起點、來源起點、長度、音量、Fade 秒數與曲線，再按「套用片段設定」。時間單位為秒；音量 100% 為原音量。
6. 點軌道名稱設定音量、靜音／隱藏或鎖定。最上方影片軌位於前景；影片的原音會一起混音。
7. 按「更新預覽」或「播放」，等待預覽處理完成後播放／暫停，拖播放滑桿或時間尺試看。修改後需要重新更新預覽。
8. Ctrl+S 儲存 `.cutmaker` 專案；「匯出 MP4／MP3」在檔案類型中選擇格式與目的地。

所有剪輯均為非破壞性：不搬動、不改寫原始素材。同一素材可重複使用，原檔須留在專案能找到的位置。

## 原型已提供

- 可新增多條影片／音訊軌，素材拖放、跨相容軌道移動、邊界吸附、兩端修剪、切割與刪除。
- 最多 100 步復原／重做，包括匯入、片段與軌道修改；開啟另一專案會清除歷史。
- 片段與軌道音量、靜音／隱藏、鎖定；片段淡入／淡出及線性、慢進、慢出、平滑 S、等功率五種曲線。
- 時間軸縮放、水平捲動、播放頭與可尋位的混合預覽。
- 專案儲存、另存與開啟；素材參照、片段、效果與軌道設定會保留。
- MP4：H.264 畫面＋AAC 立體聲；MP3：192 kbps 立體聲。匯出整條時間軸，包含空白與靜音區段。
- 匯出進度與取消。匯出使用開始時的專案內容，過程中可繼續編輯；取消／失敗保留既有目的檔，不覆蓋來源素材。
- 素材庫、預覽、屬性及時間軸的分隔線可拖拉，長檔名可換行、調欄寬及顯示完整路徑；版面會記住。

## 目前限制

這是首個可操作原型，尚未是完整剪輯產品：

- 採用先產生預覽檔、再播放的方式；編輯後按更新預覽。長專案需要處理時間，尚無即時逐軌解碼或代理素材管理。
- 輸入限上述格式，匯入時影音須可由 Windows 媒體元件讀取；優先驗證 H.264／AAC MP4、MP3、PCM WAV。圖片預設可用長度 5 秒。
- 同一軌道不允許片段重疊；多軌可重疊。先以單片段操作，尚無多選、漣漪剪輯或影音拆軌連動。
- Fade 同時作用於片段畫面透明度及原音；下方沒有影像時會淡向黑色。尚無獨立整體淡黑、自由貝茲曲線或交叉轉場。
- 切點若落在 Fade 範圍內，需先縮短／移除 Fade；外側 Fade 會在一般切割後保留。
- 預設專案為 1920×1080、30 fps；預覽最高寬 960、30 fps。尚無畫質選單、區間匯出、字幕、變速、波形、音量表、自動儲存與遺失素材重新連結 UI。

## 工具與啟動

使用專案本地 .NET SDK 10 與 FFmpeg，沒有第三方 NuGet 套件，不安裝系統解碼器包、不修改 PATH。

新電腦先在 PowerShell 執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-ffmpeg.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

SDK 與 FFmpeg 下載來源、版本、驗證及授權文件位置見 [DEPENDENCIES](docs/DEPENDENCIES.md)。工具鏈、個人素材、快取、專案和匯出檔不納入 Git。

## 驗證

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

包含 Release 建置、核心檢查、離屏 WPF 匯入／剪輯／復原／專案讀回／原生預覽播放檢查、四種尺寸的版面截圖，以及真正的 FFmpeg MP4／MP3 輸出。輸出測試會檢查編碼、時長、畫面顏色、音訊混合與淡化、取消／失敗和來源保護。

驗證輸出位於 `runtime/verification/smoke/` 與 `runtime/render-checks/`，不納入 Git。自動化會走 UI 共用操作與原生播放；不代表已完成真人滑鼠手感、長片或大量軌道的壓力驗收。

## 專案位置

- 本機主目錄：`I:\Codex\工具型\CutMaker\`
- Git：<https://github.com/sbaak85/CutMaker>，`main` 分支，沿用遠端初始提交歷史。
- `src/CutMaker.App/`：WPF 介面、匯入、編輯、預覽與媒體引擎。
- `src/CutMaker.Core/`：專案資料、驗證、非破壞剪輯與原子存檔。
- `tests/`：核心與實際媒體輸出驗證。
- `scripts/`：本地環境、建置、啟動與驗證。

後續項目見 [ROADMAP](docs/ROADMAP.md)，實作原則見 [ARCHITECTURE](docs/ARCHITECTURE.md)。
