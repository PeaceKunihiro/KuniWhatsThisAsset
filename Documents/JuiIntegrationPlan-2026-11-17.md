# JUI機能の統合検討（2026-11-17）

結論（ユーザーの目的を反映して更新）：JUIの主目的が「パッケージの内容を1フォルダーへまとめること」であるため、KWTAの第一候補は配置先を書き換えた.unitypackageを別名保存するリパッケージ機能とする。Unityプロジェクトへの直接書き込みやEditor連携は初回範囲に含めない。以下は初期検討の記録。現在の実装範囲は次節を優先する。

## 確定した実装範囲（v0.12.0）

File → フォルダーにまとめて保存から、全アセットのpathnameを書き換えた別パッケージを作成する。GNU LongLink/PAXと長いpathnameに対応し、asset・meta・GUIDを保持する。特殊なルートフォルダーは明示確認して元の配置を保持する。元ファイル・既存ファイルへの上書きは行わず、保存後に読み戻して全メンバーのハッシュと配置を検証する。

ユーザー指定により、Unity標準Importの検知・通知、AssetDatabase.Refresh / StartAssetEditing、およびその他Unity連携が必須の機能は実装対象外とする。以下に残るEditor連携・プロジェクトへの配置・復元案は比較検討の記録であり、今後の実装予定ではない。JUIのソースコードは使用していない。

## 初期検討：フォルダー集約リパッケージ

ユーザーの意図に合わせ、通常のAssets配下を指定した1フォルダーへまとめた.unitypackageを生成する。これはJUIの既存コードそのものの組み込みではなく、JUIの目的をKWTAのスタンドアロン機能として実現する案である。

```text
元: Assets/Avatar/Body.fbx
後: Assets/PackageName/Avatar/Body.fbx

元: Assets/Materials/Body.mat
後: Assets/PackageName/Materials/Body.mat
```

初回実装案は次の範囲に絞る。

1. 読み込んだパッケージを全アセット入りのまま再梱包する。部分選択は後から追加できる別機能とする。
2. まとめ先フォルダー名はパッケージ名を初期値として編集可能にする。
3. 元のasset、asset.meta、preview等は保持し、対象のpathnameを`Assets/<まとめ先>/<元のAssets直下からの相対パス>`へ変更する。
4. 既存アセット・フォルダーのGUIDとasset.meta本文は変えない。元にAssets自体のフォルダーエントリがある場合はその扱いを検査し、集約先へ対応付ける。集約先のエントリが元に存在しない場合だけ、追加するフォルダー用のGUID/.metaを新規作成する。
5. 元の.unitypackageは上書きしない。既定名を`<元名>_Grouped.unitypackage`として、出力先は保存ダイアログで指定する。
6. 一時ファイルに生成後、既存PackageReaderで読み戻す。出力パス、GUID、アセット数、新規フォルダーを除くasset/metaのハッシュ一致、GZip/TARの整合性を検証してから保存を完了する。

Unity EditorのAPIを必要とせず、既存の.NET標準GZip/TAR機能で構成できる見込みである。Unityへの導入は、生成したパッケージをユーザーが通常のImportで開く。KWTAからAssetsへの書き込み・Refresh・自動Importは行わず、現在の閲覧と静的検査を維持できる。

### 互換性の確認点

- GUIDに基づく参照は維持を狙えるが、ハードコードされたAssetsパス、Shaderのinclude等の文字列参照は、pathnameの変更だけでは追従しない。初回はasset本文を自動置換しない。
- StreamingAssets、Gizmos、Editor Default Resources、Standard Assets等、配置場所に意味があるフォルダーは一律に深くしない。対象を検出した場合は保存時に知らせ、元の配置を残す例外方式かキャンセルを選べる設計を検討する。例外を残した結果が完全な1フォルダー構成ではないことは出力計画へ明示する。[Unity公式：特殊フォルダー](https://docs.unity3d.com/2022.3/Documentation/Manual/SpecialFolders.html)、[StreamingAssets](https://docs.unity3d.com/2022.3/Documentation/Manual/StreamingAssets.html)
- ResourcesやEditor等も名前が特別なフォルダーだが、すべてがAssets直下必須というわけではない。特殊フォルダーを名前だけで同一扱いにしない。
- 既に同じGUIDを導入したプロジェクトについて、既存アセットの移動まで保証する機能とはしない。新規プロジェクトと既存GUIDありの双方で、Unity標準Importの結果を実機確認する必要がある。
- 欠落・不正な.meta、外部Package依存、元から存在するMissing参照は、再梱包によって自動修復しない。
- セキュリティ上の検出が消えることや、安全なパッケージへの変換を保証する機能ではない。ファイルは実行しない。

### UIと段階

Fileへ「フォルダーにまとめて保存…」を追加し、まとめ先名・変換後の配置例・保存先を一つのダイアログへまとめる。通常画面の右半分はプレビューを維持する。初回は全内容保持の集約のみ、その後に部分選択や複数パッケージ対応等を個別検討する。

新機能として実装する場合の版はv0.12.0候補。今回は実装していない。

## 調査対象と根拠

JUI mainのコミット`5c9398b15e71e6b2166351edfab91baac437a896`を確認。配布済みunitypackage/ZIP内のコードとの同一性比較やUnity Editor上での動作検証は行っていない。

- [JUI README](https://github.com/PeaceKunihiro/JustUnitypackageImporter2Folder/blob/5c9398b15e71e6b2166351edfab91baac437a896/README.md)
- [JUI.cs](https://github.com/PeaceKunihiro/JustUnitypackageImporter2Folder/blob/5c9398b15e71e6b2166351edfab91baac437a896/JUI.cs)
- [ライセンス原文](https://github.com/PeaceKunihiro/JustUnitypackageImporter2Folder/blob/5c9398b15e71e6b2166351edfab91baac437a896/license.txt)

JUIは、選択したassetとasset.metaを直接Assetsへ書き込み、最後にAssetDatabase.Refreshを呼ぶ設計である（JUI.cs L66–78、L1803–1889）。一時的なunitypackageの再生成を行う実装ではない。全体がUNITY_EDITOR条件内にあり、EditorWindow、AssetDatabase、EditorUserSettings等へ依存するため、WPF/.NETのスタンドアロンアプリへ無変更では組み込めない。

## 機能ごとの判断

| JUIの機能 | KWTAへの統合判断 | 必要な対応 |
| --- | --- | --- |
| パッケージ一覧・D&D・一括展開/格納 | 既存機能を利用 | 別の一覧UIを追加しない |
| ファイル/フォルダー単位の選択 | 追加可能 | 書き出しモードのチェック、中間選択、全選択/解除を追加 |
| 指定フォルダーへの配置 | 追加可能 | 元のAssets配下の相対構造を保ってassetと.metaを出力 |
| パッケージ名フォルダーに集約 | 追加可能 | 出力先への相対パス変換とフォルダー名の検証 |
| Import先Default/複数プリセット | 追加可能 | EditorUserSettingsをKWTA側の設定保存へ置換。プロジェクト別設定を区別 |
| 同名/ファイル・フォルダー/GUID競合 | 追加可能 | 書き込み計画と対象プロジェクトの.meta索引を照合 |
| バックアップ・復元・失敗時ロールバック | 追加可能 | 資産配置専用のトランザクションと復元記録を作る |
| 展開後サイズ・圧縮倍率 | 追加可能 | 既存のExpandedBytesと、通常メンバーの内容量を区別して集計 |
| GNU LongLink/PAX | 条件付きで追加可能 | KWTAの物理TARヘッダー検証を含む対応拡張が必要 |
| Unity Consoleへのログ | KWTA向けに置換可能 | 既存の警告欄と、書き出し結果の記録を使用 |
| Unity標準Importの検知・通知 | KWTA単体では直接提供できない | Unity側のEditor拡張による連携が必要 |
| AssetDatabase.Refresh/StartAssetEditing | KWTA単体では呼べない | 閉じたプロジェクトへの配置、またはEditor側連携に分ける |

これは静的な実装確認からの判断であり、Unity全バージョン・全形式の互換性を検証したものではない。

## 参考：直接書き出し・Editor連携を将来追加する場合

### 1. 選択アセットのフォルダー書き出し

最初はUnityプロジェクト外の任意フォルダーへ書き出す。例：`Assets/Avatar/Body.fbx`を、指定先`D:/Export/PackageName/Avatar/Body.fbx`と対応する.metaへ配置する。Assets直下からの相対構造を保ち、必要な祖先フォルダーの.metaも扱う。読み込みとプレビューは従来どおりで、書き出しは明示操作時のみ行う。

新規フォルダーへの出力を既定とし、既存ファイルがある場合は実行前に競合とバックアップ方針を表示する。実行結果は既存の警告欄へ統合し、通常の読み込みに確認ダイアログを増やさない。

### 2. 閉じたUnityプロジェクトへの配置

対象のUnityプロジェクトとAssets配下の配置先を指定し、同名・種別・GUID競合を調べてから書き込む。フォルダーの.metaを含め、既存のAssetsや配置先フォルダーのGUIDを不用意に置き換えない。最初はEditorを閉じたプロジェクトを対象とする方針がよい。

Unityは自動リフレッシュ等によりAssetsの変更を取り込み、コードのインポート・コンパイルも行う。したがって、開いているUnityへの書き込みはKWTA内での静的確認とは動作上の境界が変わる。[Unity公式：Asset DatabaseのRefresh](https://docs.unity3d.com/2022.3/Documentation/Manual/AssetDatabaseRefreshing.html)

KWTAはパッケージのコードを実行しないが、書き出したコードがUnity側で取り込まれることまで防ぐものではない。JUIのStartAssetEditing/StopAssetEditingによる一括制御も、外部のWPFアプリからそのまま代替できない。

### 3. 必要に応じたUnity側連携

Editor側に小さな受け取り機能を置けば、GUID検査・配置処理・RefreshをEditor内で行い、AssetDatabaseの情報を利用できる。標準Importの開始検知もEditor側に残す。連携は任意機能とし、KWTAの閲覧・書き出しにUnityやUnityEngine DLLの同梱を必須にしない。

リパッケージは現在のJUIには実装されていないが、ユーザーの目的に合わせ、今回のKWTA統合では上記の第一候補とする。以下の直接書き込み案は初期比較の記録であり、今回確定した対象範囲には含めない。

## GUIDと参照の扱い

.metaを維持すると、GUIDに基づく参照を保った配置変更が可能になる。ただし、同じGUIDが複数の場所に存在することや、依存アセットの除外は別問題である。文字列の絶対/相対パス、Resources.Load、Shaderのinclude等を使う参照まで、GUID維持だけで保証しない。JUIも依存関係の自動修復やMissing参照の修正を提供していない。

Unityはファイルとフォルダーに.metaを割り当てるため、ファイルだけでなくフォルダーのGUIDも考慮する。[Unity公式：Asset Metadata](https://docs.unity3d.com/2022.3/Documentation/Manual/AssetMetadata.html)

KWTA側では少なくとも次を区別する。

- 同じGUID・同じ配置先：既存アセットの更新候補。内容比較と上書き方針の確認。
- 同じGUID・異なる配置先：GUID競合。既存利用/除外/配置見直しを明示し、自動でGUIDを再生成しない。
- 同じ配置先・異なるGUID：既存プロジェクト側の参照を変える可能性を表示する。
- 選択から外した依存アセット：解決できる範囲で不足を通知し、自動修復を約束しない。

JUIの読込時検査はAssetDatabase.GUIDToAssetPathで既存GUIDが見つかるとファイルを選択解除する（L1684–1715）。KWTA単体では、ディスクの.metaから索引を作る必要がある。この索引はAssetDatabaseの状態と完全に等価ではなく、Editorが開いている間の変更やPackages側の資産も別途扱う必要がある。

## そのまま流用しない部分

### 読み込み処理

JUIのTarReaderは各エントリをbyte配列へ読み、UnityPackageReaderが全asset/metaを保持する（L1448–1565、L2240–2316）。KWTAは既存のストリーム読み込みと検証を基盤にする。多数のアセットをReadAssetSetでまとめてメモリへ取得するだけでは、512件や総バイト数の上限にも当たるため、大きなパッケージの書き出し用にストリーム転送を設ける。

出力は一時領域へ行い、GZip/TARの終端・CRCを含むパッケージ全体の検証後に最終出力へ反映する。閲覧後に入力が差し替わる可能性にも対応し、計画と入力内容の一致を確認する。既存の静的検査、重複・パス・リンク検証、ユーザー承認済みの読み込み上限設定を引き継ぐ。

検討開始時のKWTAはPAX/GNU拡張レコードを物理ヘッダー段階で拒否していた。System.Formats.Tarだけに任せる変更やJUIのTarReaderへの差し替えではなく、BudgetStreamの検証・件数/容量計測と整合させて拡張する。UnitypackageのGUID/asset形式ではTARメンバー名自体は通常短く、pathnameの長さとは別の問題なので、初期検討では必須としなかったが、ユーザー指定によりv0.12.0で両方に対応した。

### サイズと圧縮倍率

JUIは通常TARメンバーの内容量合計を展開後サイズとする。KWTAのExpandedBytesはTARヘッダー・パディングを含む展開ストリームの総量である。同じ値として移植しない。表示を追加するときは、内容量・展開ストリーム量・元ファイル容量を定義し、MiB単位を明確にする。圧縮倍率の通知も既存の受動的な警告欄へ合わせる。

### 書き込みと復元

JUIの復元機能はバックアップ済みファイルを戻す処理であり、成功したImportで新規作成した全ファイルを削除する完全なUndoではない（L2082–2122）。KWTAでは作成/置換したパス・元のハッシュ・出力後のハッシュを記録し、復元時に後から編集されたファイルを無条件に消さない設計がよい。

KWTAの自己更新用UpdateInstallerは、本体EXEとLibsを対象とする固定の配布形式に結び付いている。バックアップ・失敗時復元の設計は参考にできるが、Assetsへの書き出しのために更新用パス制限を緩めたり、同じ配布manifestを流用したりしない。資産配置用に独立した書き込み計画とトランザクションを設ける。

## UIと設定案

- Fileに「選択アセットを書き出す…」を追加。Unity向け配置や復元は追加ダイアログへまとめる。
- ツリーのチェック操作は書き出しモードで表示。通常のクリックによるプレビューとAlt＋左クリックの展開操作を維持する。
- 右側は引き続きモデル/画像プレビューを優先し、出力先や上書き一覧を常設しない。
- Settingに書き出し先/プリセット等をまとめる。プロジェクトごとのプリセットと、既存のディレクトリブックマークは別に扱う。
- 通知は既存警告欄へ集約し、出力先・対象・上書き内容は書き出し実行時のダイアログで確認できるようにする。

## ライセンスと配布

JUIのlicense.txtはMIT形式の許諾を記載し、改変・統合・再配布を許可している。コードを再利用する場合は著作権表示と許諾文を保持し、KWTAのHelp→ライセンスおよびLibs/licensesへ原文を追加する。Unity Editorのバイナリを本体へ組み込む方式は採用せず、KWTA側は.NET標準機能で構成できる見込みである。

現段階ではJUIコードをアプリへ取り込んでいないため、製品のライセンス一覧や配布内容も変更していない。

## 実装する場合の検証項目

- assetと.metaの内容・GUID一致、祖先フォルダー.metaの保持、既存フォルダー.metaの扱い。
- 選択/除外/中間選択、複数トップレベル集約、空フォルダー、日本語名、長いパス。
- 同名/種別/GUID競合、大小文字の衝突、パス逸脱・リンク・不正アーカイブ。
- CRC/終端不正、入力差し替え、キャンセル、容量不足、使用中ファイルでの未反映/復元。
- バックアップからの復元と、書き出し後に編集されたファイルの保持。
- 大規模パッケージで全データをメモリへ保持しないこと。
- Unity側を使う検証では、閉じたプロジェクトへの配置後、対応EditorでGUID参照とインポート結果を確認する。

既存設定の互換性を保つ新機能としてv0.12.0へ反映した。実装・検証の詳細はDevelopment.mdを参照。
