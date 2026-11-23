# SSMS 22 向け MSSQL Tool

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md)

[![Install from VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

🔥 **ダウンロード数 100 件突破 —— 応援ありがとうございます！**

## リンク

GitHub リポジトリ

https://github.com/PuLuShen/MSSQLTOOL

最新リリース（ZIP + VSIX）

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS ギャラリー（SQL Server Management Studio 向け）

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery（Visual Studio 向け）

https://www.vsixgallery.com/extension/MSSQLTool

ドキュメント（Wiki）

https://github.com/PuLuShen/MSSQLTOOL/wiki

## インストール

1. 上記の最新リリースから ZIP をダウンロードし、`安装或重装.cmd` を実行します（推奨）。
2. または、上記の SSMS ギャラリーから最新の VSIX をインストールします。
3. 更新機能を内蔵しています：**設定 → 更新** で「SSMS 終了時にインストール」を予約できます。

> **MSSQL Tool は SSMS 22（SQL Server Management Studio）向けの無料・オープンソース拡張機能です** ——
> SQL Server Management Studio のアドインとして、コンテキストを考慮した SQL 補完、T-SQL 整形、
> クエリ履歴、サーバー稼働状況の監視、結果の Excel / Google スプレッドシート / メールへのエクスポートを提供します。
>
> SSMS 22（SQL Server Management Studio 22）のユーザーは、SQL Prompt に代わる無料プラグインを探すことがよくあります。
> MSSQL Tool はまさにそのために作られました。SQL 補完・整形・結果処理を内蔵した無料の SSMS 拡張機能で、
> クエリをすばやく書き、SQL Server Management Studio の中でより高度な結果処理を行いたい SQL Server 開発者と DBA に向けています。

> MSSQL Tool は [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools)（Apache-2.0）のフォークです。SQL 補完エンジン、整形オプション、設定および多言語化のレイヤーはその上に作り直しています。

## ハイライト

- **多言語 UI（8 言語：English / 简体中文 / 繁體中文 / 日本語 / 한국어 / Deutsch / Français / Español）** —— 多くの SSMS 拡張機能は英語のみです。
- **SQL Prompt の無料代替**: SQL 補完、T-SQL 整形、クエリ履歴、Excel / メールへのエクスポート。
- **Axial SQL Tools をベース**（2016 年から継続メンテナンス）、補完エンジンと多言語化レイヤーを強化。

*注記: Google スプレッドシートへのエクスポートと GitHub スクリプト同期は実験的機能で、現在も開発中です。*

## スクリーンショット

**スマート SQL 補完** —— 入力中にコンテキストを考慮して候補を表示します。スキーマ、テーブル、ビュー、列、ストアドプロシージャ、関数、パラメーターを種類アイコン・カテゴリフィルター・選択中オブジェクトの詳細とともに提示します。

<img src="pics/1.png" width="620" alt="SELECT * FROM を入力中の SQL 補完リスト">

**設定** —— 機能ごとに独立したオプションがあり、変更は即座に保存されます。画面は SSMS の言語に従います（ここでは簡体字中国語。英語などの表示も可能です）。

<img src="pics/2.png" width="760" alt="SQL 補完タブを表示した MSSQL Tool の設定画面">

**ツールメニュー** —— 拡張機能のコマンド一覧です。ショートカットが設定されているものは併記されます。

<img src="pics/3.png" width="520" alt="SSMS のツールメニューに並ぶ MSSQL Tool のコマンド">

**結果セットの統計** —— 結果グリッドでセルを選択すると、件数・合計・平均・最小・最大・重複除外数を表示します。同じパネルで列の検索、値の検索、列の固定も行えます。

<img src="pics/4.png" width="820" alt="結果グリッド横の MSSQL Tool 統計パネル">

## 機能

### SQL 編集

- **スマート SQL 補完:** データベース、スキーマ、テーブル、ビュー、列、ストアドプロシージャ、関数、パラメーター、シノニム、リンクサーバーのオブジェクトをコンテキストに応じて候補表示します。
- **別名に追従する補完:** `c.` と入力するとそのテーブルの列だけを表示し、**列名のみ**を挿入するので、入力した別名がそのまま残ります。
- **UPDATE 対応:** `UPDATE t SET` で対象の書き込み可能な列を候補表示します。対象が後方の `FROM` 句で宣言された別名でも動作します。
- **データ型とヒント:** `INT`、`NVARCHAR(50)`、`DATETIME2` などの型や、`WITH (NOLOCK)` / `OPTION (RECOMPILE)` といった定番の書き方を適切な位置で候補表示します。
- **ポップアップ内の関数ヘルプ:** 36 以上の組み込み関数について、シグネチャ、1 行の説明、そのままコピーできる例を表示します。
- **拡張可能なキーワード:** 組み込みキーワードに加えて、設定 → SQL 補完 から独自キーワードを追加できます（1 行に 1 つ）。
- **補完用メタデータのキャッシュ:** バックグラウンドで段階的に読み込み、進捗を表示します。手動のクリア／更新、SQL Server 証明書の信頼設定にも対応します。
- **F12 定義へ移動:** データベースオブジェクトの定義を開き、SQL 内のローカル定義へもジャンプできます。
- **T-SQL 整形**（既存の操作感のために維持。SSMS 22 は標準で整形機能を持ちます。下記「整形」を参照）
- **クエリテンプレート:** プレビュー内で直接編集して Ctrl+S で保存、お気に入り、**タグ**（`-- tags: 报表, 日结`）とタグ検索（`#tag`）、選択リスト上部の**最近 10 件**。
- **スニペット**
- **`SELECT *` を列リストに展開**
- **現在のステートメントを選択**
- **ブロックコメントの切り替え**
- **クイック検索:** 複数データベースをまたぐオブジェクト検索。対象はオブジェクトエクスプローラーから引き継ぎ、スクリプト生成と新しいクエリでのオープンに対応します。
- **オブジェクト定義のスクリプト化**

### 結果セットツール

- **統計パネル（フローティング）:** 結果セット内をクリックすると自動で開き、外側をクリックすると閉じます。セルを選択すると、その範囲の**件数・合計・平均・最小・最大・重複除外数**と実際の列名を表示します。サイズ変更と位置の記憶に対応し、**先頭 N 列の固定**も可能です。
- **列検索:** 列名の一部を入力すると、その列までスクロールします（`DDZ` が `DDZT` に誤って一致することはありません）。
- **結果セットツール ウィンドウ:** 同じ検索と列ごとの統計をドッキングダイアログで提供します（ツール → *結果セット検索と統計*）。結果はワンクリックでクリップボードへコピーできます。

### 結果処理とデータ転送

- **結果グリッドを Excel にエクスポート**
- **結果グリッドをメールで送信**
- **結果グリッドを一時テーブルとしてエクスポート**
- **グリッドの列名と値を INSERT / CSV / JSON / XML / HTML としてコピー**
- **一括データ転送**

### 監視とワークフロー

- **クエリ履歴**
- **サーバー稼働状況ダッシュボード**
- **実行統計のサマリー**
- **トランザクションと Always Encrypted の警告**
- **サーバー／データベース環境ごとの接続色**
- **ミリ秒単位のクエリ実行時間**
- **結果グリッドの数値右揃え**
- **SQL Server のバージョン情報**
- **データと設定フォルダーの変更**（別ドライブ、同期フォルダー、ポータブルデバイスなど）

## 実験的機能

動作はしますが、設計はまだ流動的です。リリースごとに変更される可能性があるため、アップグレード後に関連設定を再確認してください。

| 機能 | 状態 |
| --- | --- |
| **Google スプレッドシートへのエクスポート** | ベータ —— OAuth の設定と列マッピングが変更中のため。 |
| **GitHub スクリプト同期** | ベータ —— ファイル単位のプッシュ／プルで、競合解決は手動です。 |

## 整形

SSMS 22 は独自の T-SQL フォーマッターを搭載しているため、**この拡張機能の整形機能は今後開発されません**。
コマンドは既存の操作感のために残されており、**ツール** メニュー（Ctrl+Shift+Alt+F）にありますが、新しい整形オプションが追加されることはありません。

## サポート

<img src="pics/赞赏码.png" width="320" alt="投げ銭用 QR コード" />

## FAQ

**MSSQL Tool とは何ですか？**
SSMS 22 向けの無料拡張機能で、SQL 補完、整形、結果処理を提供します。

**MSSQL Tool は無料ですか？**
はい。Apache-2.0 のオープンソースです。

**SQL Prompt や dbForge と比べてどうですか？**
MSSQL Tool は無料のオープンソースで、GitHub 同期と Google スプレッドシートへのエクスポートを内蔵しています。SQL Prompt と dbForge は商用製品です。
