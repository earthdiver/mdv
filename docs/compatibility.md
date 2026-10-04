# 互換性と既知の差異

MDVはオフラインのローカルビューアです。GitHubのリポジトリ内MarkdownファイルとQiitaの記事本文を主な対象とします。GitHubのIssueコメント等は改行・参照の処理が異なります。**完全互換・画素単位の一致・公式の互換性認証は提供していません。**

## 対応表

| 記法・機能 | GitHubモード | Qiitaモード |
| --- | --- | --- |
| CommonMarkの見出し、段落、リスト、引用、リンク、画像 | 対応 | 対応 |
| GFMの表、タスクリスト、URL自動リンク | 対応 | 対応 |
| `~~取り消し線~~` | 対応 | 対応 |
| `~取り消し線~` | 対応 | 通常テキスト |
| 段落内の通常改行 | 改行を空白相当に表示 | `<br>`として表示 |
| 脚注、見出しへのリンク | 対応 | 対応 |
| `:emoji:`、`@username` | Unicode絵文字／GitHubリンク | Unicode絵文字／Qiitaリンク |
| HTMLの`details/summary`、表、`kbd`、`ruby`等 | 許可した要素に対応 | 許可した要素に対応 |
| コードのシンタックスハイライト | highlight.js | highlight.js |
| `> [!NOTE/TIP/IMPORTANT/WARNING/CAUTION]` | 補足枠 | 通常の引用 |
| `:::note info/warn/alert` | 通常テキスト | 補足枠（文書直下） |
| `言語:ファイル名` | 通常の言語指定として扱う | ファイル名を表示 |
| `diff_言語` | 通常コード | 差分色と対応言語の色分け |
| `$...$`、`$` + バッククォートで囲む数式、`$$`、`math`フェンス | KaTeX | KaTeX |
| `mermaid`フェンス | 同梱Mermaid | 同梱Mermaid |
| YAML front matter | キー・値の表 | キー・値の表（MDV独自） |
| 単独URL | リンク | リンクカードの枠とURL |
| 色コードのスウォッチ | 通常のコード | 対応 |

## 完全に一致しないもの

- **パーサーと装飾:** MDVはremark/micromarkを使用し、GitHubのcmark-gfmやQiitaのqiita_markerそのものは実行しません。サイトと同一のCSS・Linguist/Rouge・絵文字画像ではないため、境界的な構文、言語判定、フォント、色、余白などに差があります。GFM仕様の全テストとの照合は行っていません。
- **数式:** GitHubやQiitaのMathJaxに対してMDVはKaTeXです。対応マクロ、式番号、拡張パッケージ、エスケープ処理が異なります。Qiitaの旧来のドル記法はMarkdownのエスケープと干渉しますが、MDVでは数式パーサーで直接処理します。解釈できない式は原文と診断を表示します。
- **Qiitaの補足枠:** 文書直下の独立したフェンスを対象にします。補足枠の中にはリスト・コード・補足枠を配置できますが、リストや引用の内側に置いた補足枠は対象外です。閉じ忘れは原文のまま表示します。
- **Qiitaのリンクカード・埋め込み:** OGPタイトル・サムネイルを取得せず、URLとホスト名を表示します。X、YouTube、スライド等のサービス埋め込み、外部iframe、動画・音声は再現しません。
- **GitHubのサービス連携:** `#123`、コミットSHA、PR、リポジトリ固有の参照、独自絵文字、添付ファイル、画像プロキシ、認証を必要とするリソースは再現しません。メンションは単純なユーザープロフィールへのリンクであり、存在確認や通知はしません。
- **追加の図:** PlantUML、GeoJSON、TopoJSON、STLは図として描画せず、コードと診断を表示します。Mermaidのバージョンは実サービスと独立して固定しているため、対応構文やレイアウトは異なる場合があります。
- **ローカル相対パス:** 文書と同じフォルダおよびその配下の画像・Markdownを開きます。`../`によってそのフォルダから出るパス、絶対パス、UNC、シンボリックリンク・ジャンクションは表示対象外です。GitHubのリポジトリルート基準のリンクとは異なります。
- **HTML:** スクリプト、任意CSS、iframe、イベント属性、フォーム、SVGの直接埋め込み等は削除します。各サイトの許可リストと同一ではありません。SVG画像ファイルは`img`として対応します。
- **検索:** 通常テキストとコードを検索します。HTMLタグをまたぐ連続語、Mermaid図、数式全体の検索は対象外です。ソース表示は読み取り専用です。
- **負荷の上限:** 文書は16 MiB / 4 Mi文字まで。Mermaidは1文書40個、各50,000文字・500辺まで。100,000文字を超えるコードブロックは色分けを省略します。ローカル画像は32 MiBまでです。

## 参照した一次資料

- [GitHub Flavored Markdown Spec](https://github.github.com/gfm/)
- [GitHub: Basic writing and formatting syntax](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax)
- [GitHub: Writing mathematical expressions](https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/writing-mathematical-expressions)
- [Qiita公式: Markdown記法 チートシート](https://qiita.com/Qiita/items/c686397e4a0f4f11683d)
- [Qiitaヘルプ: Markdown](https://help.qiita.com/ja/articles/qiita-markdown)
- [Qiita公式のMarkdown実装](https://github.com/increments/qiita-markdown)、特に`qiita_marker.rb`のHARDBREAKSと二重チルダ設定、`heading_anchor.rb`の見出しID
- [remark-gfmの機能と既知の挙動差](https://github.com/remarkjs/remark-gfm)

確認日: 2026-10-03。実サービスの仕様が変わった場合は、サンプルと回帰テストを追加して追従する構成です。
