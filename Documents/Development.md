# v0.1.0 技術選定・依存関係

## 初回実装の範囲

Windows向けの内容物一覧と静的な危険要素検出を実装する。モデル描画ライブラリの採用・プレビューは次段階とする。

## 採用技術

| 用途 | 採用 | 判断 |
| --- | --- | --- |
| UI | .NET 10 / WPF | Windowsネイティブのメニュー、ツリー、一覧、ファイル選択を標準機能で構成できる |
| 圧縮解除 | System.IO.Compression.GZipStream | 標準機能でストリームとして処理できる |
| TAR解析 | System.Formats.Tar.TarReader | 独自TAR展開実装を避け、標準パーサーを使用できる |
| 危険要素検出 | シグネチャ・正規表現による静的ルール | 初版で実行や外部送信を伴わず検出理由を示せる。ウイルス対策エンジンの代替ではない |
| テスト | .NETコンソール検証プログラム | 外部パッケージなしで不正入力と正常系を再現できる |

SharpCompress等の汎用アーカイブライブラリも候補となるが、初版のGZip/TAR処理は標準APIで足りるため追加しない。シグネチャとルールは本ツールに必要な警告のみを実装し、汎用のマルウェアエンジンを独自開発しない。

標準パーサーが拡張レコードを処理する前に、固定512バイトの物理ヘッダー検査でメンバー容量・項目数・種類・チェックサムを確認する。Unityの短いGUID/メンバー名に不要なPAX/GNU拡張レコードは拒否し、拡張メタデータによる先行メモリ確保を避ける。パスとGUID構造はパーサーから受け取った値に対して別途検証する。

## 安全な読み込み

- 入力は読み取り専用で開き、ディスクへの展開や一時ファイル作成を行わない。
- TARヘッダー、GZip CRCと展開サイズ、TAR終端を検証する。
- 圧縮・展開サイズ、各メンバー、項目数、アセット数、処理時間を制限する。
- 絶対パス、`..`、Windows予約名、制御文字、重複パス、リンクを拒否する。
- `asset`と`asset.meta`、プレビュー・標準外メンバーのシグネチャ、および対応テキスト形式を検査する。
- スクリプト、DLL、モデル等を実行・ロードしない。通信・外部アップロードを行わない。
- 不正入力は解析未完了として扱い、途中までの結果を解析完了として表示しない。

上限と解析対象はREADMEを参照。検出なしは安全性を保証しない。符号化・難読化や未対応の呼び出し表現、未対応形式、入れ子のアーカイブは検出範囲外となる。UTF-8として解釈できないテキストは警告する。

## ライセンス・再配布

初版に追加の外部NuGetライブラリはない。.NETのソースコードのライセンスと、Windows向けランタイム製品の配布条件は区別する。

- [.NET公式ライセンス情報](https://github.com/dotnet/core/blob/main/license-information.md)
- [.NET RuntimeのMITライセンス](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [Windows向け.NET Libraryライセンス](https://github.com/dotnet/runtime/blob/main/src/installer/pkg/LICENSE-MSFT.TXT)
- [WPFのライセンス](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)

自己完結型ではMicrosoft.NETCore.AppとMicrosoft.WindowsDesktop.Appのランタイムを同梱する。発行成果物には取得したランタイムパックのライセンス・第三者通知を同梱する。本プロジェクト自体のライセンスは未定であり、公開配布前に確定する。

## 参照した公式API資料

- [ZIP/TARアーカイブの安全な処理](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices)
- [TarReader](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarreader?view=net-10.0)
- [GZipStream](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.gzipstream?view=net-10.0)

## 検証

Releaseビルドと、合成UnityPackageを使った正常系・検出ルール・不正パス・リンク・重複・サイズ上限・件数上限・破損・途中切断・キャンセル・拡張レコードの拒否を確認する。

Releaseビルドは警告0・エラー0、自動検証37件が成功。CLIの終了コード0/1/2を確認した。

Windows GUIでは初期画面、警告を含むパッケージの読み込み、一覧、選択詳細、テキストプレビュー、検索、警告から対象への移動を確認した。最終発行版で破損GZipを開き、読み込み拒否・解析未完了表示・警告後の操作継続も確認した。ファイル選択ダイアログ内の自動入力は操作ツールの制約で完了できず、パッケージを起動引数で指定して読み込み後の画面を確認した。ドロップ操作、OS別の互換性、実際のUnityエクスポートファイルの広範な検証は未実施。
