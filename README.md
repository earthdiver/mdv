# MDV — GitHub / Qiita Markdown Viewer

GitHub形式とQiita形式を切り替えて読む、Windowsデスクトップアプリです。
C# / WPFでウィンドウ、メニュー、ファイル操作、目次、ソース表示を実装し、本文には内蔵WebView2を使用します。Webサーバー、ブラウザ、Node.jsの常駐は不要です。

**完全互換ではありません。** GFMを基本に主要な独自記法を実装していますが、実サービスの全機能・CSS・パーサーの挙動を複製するものではありません。[対応表と既知の差異](docs/compatibility.md)を参照してください。

## 起動

1. `artifacts/MDV-portable-win-x64/MDV.exe` を、書き込み可能なWindowsのフォルダにコピーします。配布に必要なのは、このEXE一つです。
2. `MDV.exe` を起動します。.NETとWebView2 Fixed Version Runtimeを内蔵し、別途のインストールは不要です。
3. 「ファイルを開く」またはドラッグ＆ドロップでMarkdownを開き、右上のGitHub / Qiitaで形式を切り替えます。

対象: Windows 10/11 x64。初回起動時は内蔵資材の展開に時間がかかります。ARM64は対応するFixed Version Runtimeを指定してビルドできますが、ARM64の実機検証は行っていません。

設定は同じフォルダの `MDV.ini` に自動生成します。設定も持ち運ぶ場合はEXEとINIを一緒にコピーしてください。

```powershell
.\MDV.exe "C:\Documents\README.md"
```

## 機能

- GitHub / Qiitaの切り替え。通常改行、専用の補足枠、コードのファイル名などをモードに合わせて描画。
- 表、タスクリスト、脚注、絵文字、HTMLの折りたたみ、シンタックスハイライト。
- KaTeXによる数式、Mermaidによる図。描画資材とフォントを同梱し、基本機能はオフラインで動作。
- 目次、日本語の見出しリンク、文書内検索、読み取り専用ソース表示。
- 複数文書を開くタブ。表示形式、倍率、検索語、閲覧位置をタブごとに保持。
- 「戻る・進む」で文書間を移動し、プレビューとソース欄の閲覧位置を復元。
- UTF-8、BOM付きUTF-16/32、Shift_JIS（CP932）。UTF-8として不正な場合だけCP932で読み込み。
- 外部エディターで保存した変更を自動反映。手動の再読み込みにも対応。
- 印刷、PDF保存、ライト／ダークテーマ、拡大縮小、最近開いたファイル。

| 操作 | ショートカット |
| --- | --- |
| 開く | Ctrl+O |
| 新しいタブ / タブを閉じる | Ctrl+T / Ctrl+W |
| 次のタブ / 前のタブ | Ctrl+Tab / Ctrl+Shift+Tab |
| 戻る / 進む | Alt+← / Alt+→ |
| GitHub / Qiita | Ctrl+1 / Ctrl+2 |
| 検索 / 次 / 前 | Ctrl+F / F3 / Shift+F3 |
| 検索を閉じる | Esc |
| ソースの表示切り替え | Ctrl+U |
| 再読み込み | F5 |
| 印刷 | Ctrl+P |
| 拡大 / 縮小 / リセット | Ctrl++ / Ctrl+- / Ctrl+0 |

PDF保存は「ファイル」メニュー、外部画像・テーマ・目次の切り替えは「表示」メニューにあります。

「ファイルを開く」、最近開いたファイル、ドラッグ＆ドロップは、新しいタブに文書を開きます。複数ファイルの選択・ドロップにも対応し、空のタブが選択されていればそのタブを使用します。同じファイルが既に開かれている場合は、そのタブに切り替えます。タブの×または中央クリックで閉じられ、最後のタブを閉じると空のタブが残ります。

Markdownリンクの通常クリックは同じタブで移動します。Ctrl＋クリックまたは中央クリックでは、新しいタブに開いて切り替えます（既に開いているファイルは既存タブを選択）。各タブのツールバーの矢印または「表示 → 戻る／進む」で、元の文書の読んでいた位置へ戻れます。再読み込みでは履歴を増やさず、読み込み失敗時は現在の文書と履歴を維持します。戻った先から別の文書を開くと、そこから先の「進む」履歴は置き換わります。

タブを切り替えるとプレビュー・ソース欄の位置、折りたたみ状態、GitHub／Qiita、倍率、検索欄が復元されます。別のタブを見ている間のファイル変更は、その文書のタブに戻ったときに反映します。元ファイルが削除されていても、開いているタブには前回読み込んだ内容を表示します。タブと閲覧履歴は起動中のみ保持し、履歴はタブごとに最大100件です。

Markdown本文を外部の変換APIへ送信しません。外部画像は初期状態で無効で、表示メニューから有効にすると画像URLへ通信します。HTTP(S)・メールリンクはクリック時に既定のアプリを開きます。ローカル画像とMarkdownリンクは、開いた文書のフォルダ内を対象にします。親フォルダやジャンクション・シンボリックリンクの参照は扱いません。

アプリの設定保存・登録にはレジストリを使いません。設定・最近開いたファイルはEXEの隣のUTF-8 INIに保存し、文書自体は書き換えません。描画資材は一時フォルダに展開し、WebView2の作業データは正常終了時に削除します。WindowsやWebView2内部のレジストリ参照・OSが記録する履歴まで禁止するものではありません。[保存場所・INIの書式・ポータブル動作の範囲](docs/portable.md)も参照してください。

## ソースからビルド

必要なもの: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)、[Node.js 22以降](https://nodejs.org/)。初回ビルドにはnpm/NuGetとMicrosoftのWebView2配布先への接続が必要です。

WindowsのPowerShellで:

```powershell
.\scripts\build.ps1
# ダウンロード済みのFixed Version Runtimeを使用する場合
.\scripts\build.ps1 -FixedRuntimeDirectory C:\SDK\WebView2Fixed-x64
# ARM64向けを作る場合（対応するランタイム一式を指定）
.\scripts\build.ps1 -Runtime win-arm64 -FixedRuntimeDirectory C:\SDK\WebView2Fixed-arm64
```

開発用の実行（開発PCにはWebView2 Evergreen Runtimeが必要）:

```powershell
npm ci
npm run build
dotnet run --project src/Mdv.App
```

配布版は `scripts/build.ps1` で作成します。描画資材・ランタイム・ライセンスを `tools/Mdv.Pack` でBrotli圧縮し、`PortableBuild=true` でEXEに埋め込みます。.NETの単一ファイル圧縮も有効です。WebView2の取得バージョンとSHA-256は `scripts/webview2-runtime.json` で固定しています。ランタイムを更新するときはこの定義を更新して再ビルドしてください。

Linuxからも `EnableWindowsTargeting` によるクロスビルドが可能ですが、CABの展開とWPFアプリの実行検証にはWindowsを使用します。WebView2の描画資材は `npm run build` で `src/Mdv.App/Renderer` に生成します。

## 検証

```powershell
npm test
dotnet run --project tests/Mdv.Core.Tests -c Release
npm run build
npx playwright install chromium
npm run test:browser
# 配布版をWindowsで起動し、WPF + WebView2 + PDFを確認
.\scripts\smoke-windows.ps1
```

テストは記法差、HTMLの無害化、図と数式の実描画、画像の通信制御、見出し・脚注リンク、検索、文字コード、パス検証、タブと閲覧履歴、INI、資材の整合性検証と展開を対象にします。Windows統合テストはタブ切り替え時の位置・設定復元、履歴の独立、複数ファイルのオープン、更新・削除、キーボード操作を確認します。さらにEXEだけを別のフォルダにコピーし、同梱ランタイムの使用、INIの生成、移動・改名後の設定復元、一時プロファイルの削除、書き込み不可時の通知も検証します。結果は `artifacts/native-smoke.*` と `artifacts/portable-*.json` に保存します。

`scripts/test-server.mjs` は自動テスト用で、アプリには同梱しません。

## 構成

| 場所 | 内容 |
| --- | --- |
| `src/Mdv.App` | WPF UI、WebView2、ファイル監視、印刷、アプリ内通信 |
| `src/Mdv.Core` | 文字コード、ファイル読み込み、相対パス、設定 |
| `renderer` | GFM / Qiitaの変換と内蔵プレビュー |
| `samples` | モード差を試せるサンプル |
| `tests` | コア機能の回帰テスト |
| `scripts` | 描画資材生成、Windows配布ビルド、統合テスト |
| `tools/Mdv.Pack` | 埋め込み資材のBrotli圧縮・SHA-256生成 |

アプリのソースはMITライセンス。依存ライブラリ・ランタイムにはそれぞれのライセンスが適用されます。描画ライブラリのライセンス文はビルド時に `Renderer/THIRD-PARTY-NOTICES.txt` へ集約し、.NET・WebView2のライセンスもEXEに同梱します。「ヘルプ → 同梱ライセンス」から参照できます。GitHub・Qiitaの公式アプリではありません。
