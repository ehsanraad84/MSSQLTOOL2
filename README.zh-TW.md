# MSSQL Tool：SSMS 22 效率擴充功能

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文** | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md)

[![從 VSIX Gallery 安裝](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

🔥 累計下載已突破 100+ 次，感謝開發者們的支持！

## 連結

GitHub 儲存庫

https://github.com/PuLuShen/MSSQLTOOL

最新發行（ZIP + VSIX）

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS 圖庫（適用於 SQL Server Management Studio）

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery（適用於 Visual Studio）

https://www.vsixgallery.com/extension/MSSQLTool

使用文件（Wiki）

https://github.com/PuLuShen/MSSQLTOOL/wiki

## 安裝

1. 從上方的「最新發行」下載 ZIP，執行 `安装或重装.cmd`（建議）。
2. 或從上方的「SSMS 圖庫」下載最新 VSIX。
3. 擴充功能內建更新檢查：**設定 → 更新** 可一鍵排程「關閉 SSMS 時安裝」。

> **MSSQL Tool 是一款免費、開源的 SSMS（SQL Server Management Studio）擴充功能，適用於 SSMS 22** ——
> 作為 SQL Server Management Studio 的插件，它為 SSMS 帶來可感知內容的 SQL 智慧補全、T-SQL 格式化、
> 查詢歷史、伺服器健康監控，以及將結果匯出到 Excel / Google 試算表 / 郵件。
>
> SSMS 22（SQL Server Management Studio 22 版）的使用者常常在找可以替代 SQL Prompt 的免費插件；
> MSSQL Tool 正是為此而生：一個內建 SQL 補全、格式化與結果處理的免費 SSMS 擴充功能，
> 面向希望在 SQL Server Management Studio 內更快編寫查詢、完成更多結果處理的 SQL Server 開發者與 DBA。

> MSSQL Tool 是 [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools)（Apache-2.0）的 fork，在此基礎上重構了 SQL 自動補全、格式化選項以及設定與多語言層。

## 亮點

- **多語言介面（8 種語言：English / 简体中文 / 繁體中文 / 日本語 / 한국어 / Deutsch / Français / Español）** —— 多數 SSMS 插件的介面只有英文。
- **SQL Prompt 的免費替代**：SQL 智慧補全、T-SQL 格式化、查詢歷史，以及匯出到 Excel / 郵件。
- **基於 Axial SQL Tools**（2016 年起持續維護），並強化了補全引擎與多語言層。

*說明：Google 試算表匯出與 GitHub 指令碼同步仍屬實驗性功能，正在開發中。*

## 介面截圖

**智慧 SQL 自動補全**——邊輸入邊提示：結構、資料表、檢視、欄位、預存程序、函式、參數，帶類型圖示、分類篩選，以及目前選取對象的詳細資料。

<img src="pics/1.png" width="620" alt="輸入 SELECT * FROM 時的 SQL 補全清單">

**設定**——每項功能都有獨立選項，儲存後立即生效；介面跟隨 SSMS 語言（此處為簡體中文，也可切換為英文等其他語言）。

<img src="pics/2.png" width="760" alt="MSSQL Tool 設定視窗的 SQL 自動補全頁">

**工具選單**——擴充功能提供的全部命令，設定了快速鍵的會一併顯示。

<img src="pics/3.png" width="520" alt="SSMS 工具選單中的 MSSQL Tool 命令">

**結果集統計**——在結果方格中選取儲存格，即可看到筆數、合計、平均、最小、最大、相異值；同一面板還能定位欄位、尋找值和固定欄位。

<img src="pics/4.png" width="820" alt="MSSQL Tool 結果集統計面板與結果方格">

## 功能

### SQL 編輯

- **智慧 SQL 自動補全：** 依內容提示資料庫、結構、資料表、檢視、欄位、預存程序、函式、參數、同義字與連結伺服器物件。
- **跟著別名走：** 輸入 `c.` 只列出該資料表的欄位，並插入**純欄位名**，你打的別名保持原樣。
- **UPDATE 支援：** `UPDATE t SET` 會提示目標資料表的可寫入欄位，即使目標別名寫在後面的 `FROM` 裡也有效。
- **資料類型與提示：** 在合適的位置提示 `INT`、`NVARCHAR(50)`、`DATETIME2`，以及 `WITH (NOLOCK)`、`OPTION (RECOMPILE)` 等常用寫法。
- **補全視窗內顯示函式用法：** 36+ 個內建函式提供**簽章 + 說明 + 可直接複製的範例**。
- **可擴充關鍵字：** 設定 → SQL 補全 中可追加自訂關鍵字（每行一個）。
- **補全中繼資料快取：** 背景分步載入並顯示進度、手動清除與重新整理，並可選擇信任 SQL Server 憑證。
- **F12 移至定義：** 開啟資料庫物件定義指令碼，並支援跳轉到 SQL 中的本機定義。
- **T-SQL 格式化**（為相容舊習慣保留；SSMS 22 已內建格式化，見下文「格式化」）
- **查詢範本：** 預覽內直接編輯並 Ctrl+S 儲存、我的最愛、**標籤**（`-- tags: 报表, 日结`）與 `#標籤` 檢索、選擇器頂部**最近 10 筆**。
- **程式碼片段**
- **將 `SELECT *` 展開為欄位清單**
- **選取目前語句**
- **切換區塊註解**
- **快速搜尋：** 跨資料庫物件搜尋，目標自動取自物件總管，支援產生指令碼與在新查詢中開啟。
- **產生物件定義指令碼**

### 結果集工具

- **浮動統計面板：** 在結果集中點擊時自動出現、點擊結果集以外自動收起。選取儲存格後顯示該選取範圍的**筆數 / 合計 / 平均 / 最小 / 最大 / 相異值**，並標明**真實欄位名**；面板可拖曳調整大小、**記住尺寸與位置**，還支援**固定前 N 欄**。
- **欄名跳轉：** 輸入欄名的一部分即可讓結果集捲動到該欄（`DDZ` 不會誤跳到 `DDZT`）。
- **結果集工具視窗：** 同樣的尋找 + 逐欄統計，位於 工具 → *結果集搜尋與統計*，可一鍵複製統計結果。

### 結果處理與資料傳輸

- **將結果方格匯出到 Excel**
- **透過郵件傳送結果方格**
- **將結果方格匯出為暫存資料表**
- **複製方格欄位名與儲存格值，支援 INSERT、CSV、JSON、XML、HTML 格式**
- **批次資料傳輸**

### 監控與工作流程

- **查詢歷史**
- **伺服器健康面板**
- **執行統計資訊摘要**
- **交易與 Always Encrypted 警告**
- **依伺服器與資料庫顯示連線色彩**
- **毫秒級查詢執行時間**
- **結果方格數字靠右對齊**
- **SQL Server 版本資訊**
- **可自訂資料與設定目錄**（可指向其他磁碟、同步資料夾或可攜裝置）

## 實驗性功能

這些功能可用，但仍在演進：不同版本之間行為可能改變，升級後請重新檢查相關設定。

| 功能 | 狀態 |
| --- | --- |
| **Google 試算表匯出** | Beta —— OAuth 設定與欄位對應仍在調整。 |
| **GitHub 指令碼同步** | Beta —— 以整檔推送／拉取，衝突需手動處理。 |

## 格式化

SSMS 22 已內建 T-SQL 格式化功能，因此**本插件的格式化功能不再繼續更新**。
命令仍然可用、並為相容舊習慣保留 —— 現已移到 **工具** 選單（Ctrl+Shift+Alt+F）—— 但不會再增加新的格式化選項。

## 贊助

<img src="pics/赞赏码.png" width="320" alt="贊助 QR Code" />

## 常見問題（FAQ）

**MSSQL Tool 是什麼？**
一個免費的 SSMS 22 擴充功能，提供 SQL 智慧補全、T-SQL 格式化與結果處理。

**MSSQL Tool 免費嗎？**
免費，基於 Apache-2.0 開源。

**和 SQL Prompt、dbForge 相比如何？**
MSSQL Tool 免費且開源，內建 GitHub 同步與 Google Sheets 匯出；SQL Prompt 與 dbForge 是商業產品。
