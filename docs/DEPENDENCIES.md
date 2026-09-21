# 工具與安裝紀錄

## 2026-09-20

新增到本專案的必要開發工具：

| 項目 | 版本 | 位置／用途 |
| --- | --- | --- |
| Microsoft .NET SDK | 10.0.401 | `.tools/dotnet/`，C#／WPF 建置與測試 |
| .NET Runtime／Windows Desktop Runtime | 10.0.12 | 隨 SDK 一起提供，執行桌面程式 |
| ASP.NET Core Runtime | 10.0.12 | SDK 發行包內含，本專案未使用 |

來源為 Microsoft 官方 [dotnet-install](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script) 安裝腳本：

- 腳本：`https://dot.net/v1/dotnet-install.ps1`
- 本次 SDK：`https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip`
- 腳本 SHA-256：`E8B873E18A81E5C4CD8AB69D84DAC8FEAD291D50B3C44633CD7FDDAD709A13D6`（僅本次紀錄，官方入口內容日後可能更新）
- 安裝路徑：`I:\Codex\工具型\CutMaker\.tools\dotnet\`
- 未寫入系統 PATH。啟動與建置腳本使用專案本地工具路徑；SDK 與所有生成物均被 Git 忽略。

首次 SDK 初始化曾顯示 HTTPS 開發憑證安裝訊息。後續唯讀 `Cert:\CurrentUser\My` 與 `dotnet dev-certs https --check --verbose` 驗證皆未發現憑證。後續腳本已設 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` 與關閉 CLI 遙測。

目前沒有第三方 NuGet 套件。Git 使用電腦既有的安裝；FFmpeg 的新增紀錄如下。

未來每次新增工具或套件，追加名稱、版本、來源、位置、用途與授權文件位置，並告知使用者。


## 2026-09-20：剪輯／匯出原型新增

| 項目 | 版本 | 本地位置／用途 |
| --- | --- | --- |
| FFmpeg essentials Windows x64 | 9.0.2 | `.tools/ffmpeg/bin/ffmpeg.exe`，多軌合成、混音、Fade、預覽與 MP4／MP3 輸出 |
| ffprobe | 9.0.2 | `.tools/ffmpeg/bin/ffprobe.exe`，讀取精確匯入時長、檢查來源音／視訊串流及實際輸出資訊 |
| ffplay | 同發行包 | 發行包內附，本程式不使用 |

- 使用 [FFmpeg 官方下載頁](https://ffmpeg.org/download.html) 列出的 [Gyan Windows builds](https://www.gyan.dev/ffmpeg/builds/)。
- 固定版本套件：`https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip`
- 原始入口：`https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip`
- SHA-256（已驗證完整下載包）：`60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA`。
- 下載連線較慢，最後將已下載前段與 HTTP Range 分段續傳組合；整份雜湊與官方檢查碼一致後才解壓執行。
- 安裝在 `I:\Codex\工具型\CutMaker\.tools\ffmpeg\`；啟動腳本只設定本次程序的 `CUTMAKER_FFMPEG_DIR`，不寫系統 PATH、不加系統解碼器包。
- 原發行包 GPL v3 授權文件保留在 `.tools/ffmpeg/LICENSE`，原說明保留在 `.tools/ffmpeg/README.txt`。工具原始碼及發行來源見上方連結。
- Git 不包含 FFmpeg 執行檔或下載包。新環境執行 `scripts/setup-ffmpeg.ps1` 下載固定版本並核對相同雜湊。

## 2026-09-21：此電腦首次安裝

- 專案位置：`C:\Users\sbaak.fang\ChatGPT\CutMaker`。
- 依使用者指定，Microsoft .NET SDK 10.0.401 安裝於 `tools/dotnet/`，來源為既有 setup 腳本使用的 Microsoft 官方 dotnet-install；已執行 --list-sdks 確認版本。
- 隨附 Microsoft.NETCore.App、Microsoft.WindowsDesktop.App、Microsoft.AspNetCore.App 10.0.12；授權文件位於 `tools/dotnet/LICENSE.txt` 與 `ThirdPartyNotices.txt`。
- FFmpeg／ffprobe 9.0.2 安裝於 `.tools/ffmpeg/`，沿用上列固定 Gyan 發行來源與 SHA-256，下載包雜湊核對通過；授權保留於 `.tools/ffmpeg/LICENSE`。
- 啟動環境優先使用 `tools/dotnet`，保留舊 `.tools/dotnet` 相容性；新 SDK 安裝腳本改用 `tools/dotnet`。兩個工具目錄均由 Git 忽略。
- 修正 SDK 安裝成功但 LASTEXITCODE 為空時誤報失敗的判斷，改檢查指定版本 SDK 檔案是否存在。
- 未修改系統 PATH、未安裝系統解碼器包。

## 2026-09-21：跨專案共用工具

- SDK 10.0.401 搬至 `C:\Users\sbaak.fang\ChatGPT\.tools\dotnet10`，FFmpeg 9.0.2 搬至同層 `ffmpeg`；未重新下載，保留既有 dotnet6，授權文件隨工具搬移。
- environment.ps1、setup.ps1、setup-ffmpeg.ps1 使用專案父目錄的 `.tools`；SDK 使用 dotnet10，啟動保留舊專案 .tools/dotnet 相容性。
- 下載包與解壓暫存搬至共用 cutmaker-downloads、cutmaker-ffmpeg-9.0.2-unpacked；安裝腳本搬至 dotnet10-install.ps1。
- 專案 .tools/dotnet-home 為 CLI 資料；.tools/ 與 tools/ 均由 Git 忽略。tools/dotnet 僅餘被程序占用的空目錄，已確認無檔案，暫保留。未修改系統 PATH。
- 共用 SDK Release 建置通過，0 警告、0 錯誤；未執行完整影音驗證。
- 直接呼叫 build-server shutdown 時 SDK 首次初始化顯示 HTTPS 開發憑證安裝訊息，未執行 trust；正常專案腳本仍停用自動憑證與遙測。
