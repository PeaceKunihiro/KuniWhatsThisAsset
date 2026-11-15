# 技術選定・依存関係

## v0.8.0 モデル表示

SharpAssimp 6.0.12（版とlockファイルを固定）でFBX/OBJを解析し、標準WPF Viewport3Dで描画する。専用Shaderの実行は行わず、Unityマテリアルの主画像・色・透過マスクと簡易照明でアニメ調の見た目へ近似する。現行の依存と再配布条件はTHIRD-PARTY-NOTICES.mdを参照。

モデル解析は同じEXEの隠しworkerモードを別プロセスで起動し、匿名パイプでデータを受け渡す。カスタムIOはパッケージから渡されたメモリ内モデル/MTLだけを提供し、パッケージ内DLLはロードしない。同梱Assimpは絶対パスで指定する。日本語モデル名/MTL参照は内部ASCII別名へ変換する。プロセス分離はOSサンドボックスではなく、60秒・約2 GiBの監視と終了で障害の影響を抑える。

100,000三角形以上または依存材質10以上なら、解析後・WPFメッシュ生成前に確認する。パッケージ側の容量上限解除はこの確認やプレビュー独自上限を変更しない。UVと階層変換・法線を受け取り、材質/画像はGUID参照でバッチ読み込みする。テクスチャは最長辺2,048、総32 Mi画素、対応画像入力は4K正方形を含む16,777,216画素へ調整。従来の画像入力上限1,600万画素の記述はこの値で置き換える。

Fuzzy ver.2.00とLuchika ver.1.00で実データ検証済み。実データ検証はUIテストの--real-modelsで明示実行し、ユーザーデータをソースや配布物へ含めない。以下の初版記録は当時の判断として保持する。

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

## v0.3.0の変更

GUIは読み込み容量・件数・解析時間の上限で確認し、承認した上限だけを今回2倍にして先頭から再解析する。既定のScanLimitsレコードは変更せず、次の読み込みに持ち越さない。否認時は中止する。CLIの対話は行わない。テキストのサンプル予算は変更しない。不正構造・破損の検証は上限変更後も維持する。

アイコンの元画像はIMGs/KWTA_icon.png。EXE用の複数解像度ICOを同じ画像から作成し、追加ライブラリなしでWPFのResourceとApplicationIconを使う。ブラウン・クリームを基調に、紫を選択色に使用する。

## v0.4.0の変更

GUIで継続を承認した読み込みにはIgnoreReadLimitsを適用し、容量・件数・解析時間の上限をすべて無効化する。次回の読み込みや既定のScanLimitsは変更しない。解析サンプルの予算、構造・CRC検証、キャンセルは維持する。

警告欄はGridSplitterで高さを変えられるGrid行へ配置。同じWPFビューを独立ウィンドウへ移動し、復帰時に元のContentControlへ再格納する。WPFテストで高さの変更、繰り返し分離・復帰、共有ビューの更新、正常時の非表示を確認した。マウスによる実ドラッグの確認は別途行う。

## v0.5.0 技術検証・採用

追加OSSは不要。Shift-JISには.NET標準のCodePagesEncodingProvider（CP932）、画像にはWPF標準の明示的なPNG/JPEG/BMP/GIF/TIFF/ICOデコーダーを使用する。Windowsランタイムの既存ライセンス条件で扱い、追加NuGetパッケージは導入しない。

- [公式 CodePagesEncodingProvider](https://learn.microsoft.com/en-us/dotnet/api/system.text.codepagesencodingprovider)
- [公式 WPF Imaging Overview](https://learn.microsoft.com/en-us/dotnet/framework/wpf/graphics-multimedia/imaging-overview)
- [公式 BitmapDecoder](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.bitmapdecoder)

画像は選択時にアーカイブを再走査・検証するため、大きいパッケージでは表示まで時間がかかる。現在の読み込み承認設定を引き継ぐ。画像入力自体は64 MiBまで、復号前の寸法確認後、各辺16,384かつ合計1,600万画素以内の先頭フレームをBGRA32へコピーしてストリームから独立した画像にする。選択変更時は読み込みをキャンセルし、遅れて終了した結果を表示しない。TGA/DDS/PSD/SVGは未対応。デコーダー自体の処理中断は行えず、キャンセルは復号の前後で確認する。

WPF検証で形式別の復号と破損拒否、パッケージを作成して画像を選択・表示しテキストへ切り替える経路を確認。解析テストではCP932日本語とコード検出、BOM・サンプル末尾、完全なアセット取得と容量制限・破損検証を確認した。

## v0.6.0 ライセンス表示

Help→ライセンスで、src/KuniWhatsThisAsset.App/Licenses/の原文をEmbeddedResourceから読み取って表示する。同じソース文書をContentとして出力・発行先のlicenses/へコピーし、表示内容と配布物の不一致を防ぐ。今回収録した文書は.NET 10.0.15のRuntime・WindowsDesktopランタイムパック由来のライセンス・第三者通知、および取得済みWindows .NET SDKの.NET Libraryライセンス。依存やランタイムの更新時には対象バージョンの文書を再確認する。

## v0.7.0 更新確認

追加パッケージなしでHttpClient・System.Text.Json・ファイル排他を使用する。更新確認は指定GitHubに対するユーザー起点の通信で、パッケージ解析の外部送信禁止と区別する。状態保存はLocalApplicationData配下、レスポンス制限は1 MiB/1,000項目、タイムアウト5秒、自動再試行・リダイレクト追跡なし。ダウンロード先は固定したリポジトリのraw URLと完全一致する候補に限定する。詳細と検証範囲はUpdateCheckPlan.md。

## v0.9.0 Prefab・レンダリングキャッシュ・読み込み設定

PrefabAnalysisはUnity YAMLの既知フィールドだけを読み、PrefabPreviewBuilderがパッケージ内参照を解決する。外部スクリプト・DLLは実行しない。直接Rendererは有効状態・祖先非表示・Mesh名/旧fileID表・材質スロットを使用する。FBXインスタンスは元モデルの初期形状のみを用い、差分の未反映を表示する。入れ子参照は深さ8、訪問32、元モデル16まで。循環参照は拒否する。Unity組み込みMeshやファイル不足は説明して省略する。

ModelViewport.CaptureViewsは背景や操作UIを含まない独立Viewport3DとOrthographicCameraで1,024pxの正面/右側面/上面PNGを生成する。RenderCacheは固定長ハッシュ名の平坦なファイル群のみを管理し、再帰削除を行わない。パッケージ全体SHA256・選択GUID・描画改訂番号をキーにし、PNGハッシュと完了マニフェストを検証する。読書き・LRU削除はユーザープロファイル別の名前付きMutexで排他制御する。最終参照時刻はマニフェストmtimeとし、PNG三枚とJSONの総サイズで容量を管理する。

UserPreferences/PreferenceStoreで上限・履歴10件・フォルダーブックマークをJSON保存する。既存の更新確認日ファイルとは別ファイルとする。初期容量はpackage/cacheとも500 MiB。パッケージ容量の無制限設定はMaxPackageBytesのみ変更し、継続確認での今回全上限解除とは区別する。Settingの保存中に読み込みが終了しても新しい履歴を保持する。

★ボタンの単一クリックはOSのダブルクリック時間を待って追加し、二重クリック時は追加タイマーを停止して削除する。ブックマークは物理ディレクトリを指し、開く操作ではそのフォルダーを初期位置にしたパッケージ選択ダイアログを表示する。

Fuzzy/Luchika実Prefabで三方向の透過写真を目視確認。通常テストは独立したartifacts内プロファイルと自作モデルを使用し、ユーザー設定・キャッシュを汚さない。

参考：[Unity YAML Prefab serialization](https://docs.unity.com/en-us/engine/6000.0/manual/working-with-scenes/text-scene-format/yaml-prefab-serialization)、[Unity YAML scene example](https://docs.unity.com/en-us/engine/6000.0/manual/working-with-scenes/text-scene-format/yamlscene-example)

## v0.9.1 材質解決の共通化

MaterialResolverをFBX/OBJの直接表示とPrefabPreviewBuilderで共有する。明示GUIDを優先し、命名設定0/1/2、検索設定Local/RecursiveUp/Everywhereを扱う。Everywhereもパッケージ内に限定する。既存のモデル配下に一意の材質がある構成への補完を維持する。近いMaterialsから順に探索し、同順位の複数候補は自動選択しない。

名前から解決しない場合は、パッケージ内Prefabの直接Rendererを読み、同じ元モデルのMesh ID/ノード名・スロットから材質GUIDを補完する。推定では全Prefabを確認し、異なるGUIDがあれば未解決とする。件数上限32・各16 MiB・合計64 MiBを超える場合や、未対応Prefabが混じる場合は推定を省略する。元ファイルの書き換えや外部画像パスの読み込みは行わない。

ModelMaterials.LoadDetailedで未解決・画像デコード失敗の説明を返し、ModelPreviewがプレビューとキャッシュの注記へ反映する。RenderCacheの改訂番号はv0.9.1-1とし、旧版の単色写真を再利用しない。

通常UIテストに親探索・Local制限・近傍優先・GUID優先・命名・Prefab補完・競合拒否を追加。--texture-modelsでユーザー提供のAkyo/ネコメイドの6経路を、本番の解決処理のみで検証できる。

参考：[Unityの列挙値定義](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/AssetPipelineEditor/Public/ModelImporting/ModelImporter.bindings.cs)、[親方向のMaterials探索](https://docs.unity3d.com/ja/6000.0/ScriptReference/ModelImporterMaterialSearch.RecursiveUp.html)
