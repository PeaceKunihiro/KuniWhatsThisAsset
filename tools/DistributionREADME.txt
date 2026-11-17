KuniWhatsThisAsset v{{VERSION}} / Windows x64

KuniWhatsThisAsset.App.exeを起動してください。
.NET/WPF/モデル解析ライブラリはEXEへ内蔵されています。
Libsにはライセンス原文と配布ファイル一覧を保持します。EXEとLibsは一式で使用してください。
内蔵ネイティブライブラリは.NETがユーザーの一時フォルダーへ展開して使用します。
モデル解析にはMicrosoft Visual C++ v14 x64ランタイムが必要です。
Microsoft公式インストーラー: https://aka.ms/vc14/vc_redist.x64.exe

.unitypackageを開き、左のツリーから画像/FBX/OBJ/Prefabを選んでプレビューします。
Alt+左クリックでツリーを一括展開/格納。モデルは左ドラッグ回転、右/中ドラッグ移動、ホイールでズーム。
100,000三角形以上または依存材質10以上は描画前に確認します。
Prefabの材質・表示状態の差分、静止バインド姿勢に対応します。
Prefab配置・Boneアニメーション・Unity専用Shaderの完全再現は未対応です。

Help→バージョン情報から「更新を確認する」で手動確認できます。
自動確認は1日1回、手動確認には日次制限がありません。
更新があれば「ダウンロードして更新・再起動」で最新版ZIP/SHA256を取得し、検証後に置換して再起動します。
設定/履歴/ブックマーク/キャッシュは上書きしません。失敗時は現在のアプリを保持し、置換途中の失敗は元へ復元します。
書き込み権限のあるフォルダーで使用してください。更新作業はユーザー別のUpdatesフォルダーで行います。
ネットワークや配布形式の問題で自己更新できない場合は、ダイアログ内のStableリンクから入手できます。

初めて旧版から切り替えるときは、新しいフォルダーへZIPを展開すると以前の多数のDLLが残りません。
以前の設定はそのまま引き継がれます。今後の自己更新では配布一覧に基づき不要になった未変更ファイルを整理します。

ユーザーデータ: %LOCALAPPDATA%/KuniWhatsThisAsset/
preferences.json（設定/履歴/ブックマーク） / RenderCache（透過写真）
Updates（更新作業。成功した再起動後に整理し、失敗時は復旧用バックアップを保持）
Help→ライセンスから原文を確認できます。
Stable: https://github.com/PeaceKunihiro/KuniWhatsThisAsset/tree/master/Stable

File→フォルダーにまとめて保存…で、全内容とGUID/metaを保持した別名の.unitypackageを作成できます。
GNU LongLink/PAX/USTARと長いパスに対応します。保存後にCRC/配置先/メンバー内容を検証します。
特殊フォルダーは元位置を保持する確認が必要です。固定パス参照の修正は行いません。
Unity標準Import検知、AssetDatabase操作、Editor連携、プロジェクトへの直接配置は実装していません。
元パッケージと既存の出力先は上書きしません。保存先には新しい名前を指定してください。
