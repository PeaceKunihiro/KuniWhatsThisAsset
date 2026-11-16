# FUWATTOSYUGAENZYERU v26 参照エラー調査（2026-11-16）

対象：ユーザー提供の「餅亭 FUWATTOSYUGAENZYERU all v26.unitypackage」（460,618,635 bytes）。スクリーンショットの「あのめあMA.prefab」を再現対象とした。元パッケージは読み取りのみで、解析用データ・画像・プロファイルはartifacts/fuwatto-analysis/へ保存する。

## 結論

画面を覆っていた大量の出力は、画像がすべて欠損しているという意味ではない。主因は、現行プレビュー側の補完上限と未対応のPrefab材質差分。主画像の参照はパッケージ内で成立している。

今回の改修はエラー・注記を警告欄へ統合する表示修正であり、未対応の材質差分や参照探索の仕様変更は行っていない。v0.9.2でも本データの材質は未解決のものが残る。

## パッケージ構成

| 項目 | 件数 |
| --- | ---: |
| GUIDアセット（フォルダーを含む） | 788 |
| Prefab | 414 |
| FBX | 56 |
| Material | 42 |
| PNG | 85 |

Prefab全体は約20 MiB。選択Prefabからm_SourcePrefabをたどった範囲は6個のPrefabで、たどった参照先に欠損はなかった。パッケージ全体の414件と選択モデルに関連する6件は区別すべきである。

対象：Assets/もち亭/ふわっとしゅがー♡えんじぇぅ/あのめあ/あのめあMA.prefab

直接の参照先は、あのめあ/FBX.fbxとPet/たまごはね&プティング.prefab。Petから別のPrefabやFBXへつながる。

## 1. Prefab補完の32件上限

v0.9.1のMaterialResolverは、名前で材質を特定できない場合にパッケージ内Prefab全体から割り当てを推定する。この推定は全件を確認できることを条件とし、32件を超えると開始しない。

本データは414件のため、選択Prefabに関連する数が少なくても「Prefab補完の解析件数上限を超えています」が各材質で発生する。これは画像欠損の検出ではなく、推定処理を実行していないことの通知。

上限を無条件に引き上げても、以下の材質差分や色違いの競合が残る。選択Prefabと依存範囲を識別した参照インデックスが先に必要。

## 2. Material/Matフォルダーと名前の相違

主な材質の配置は以下。

- 衣装：ルートのMaterial/メインA.mat、メインB.mat、透過a.mat、透過B.mat、宝石.mat、ファー.mat
- ペット：Pet/Mat/からだ.mat、目.mat、透過.mat 等
- パーティクル：Pet/particle/tex/ 内

標準のMaterialsというフォルダー名ではなく、モデルフォルダーとは別の共有位置にある。FBX内には「メインA.001」「メインB.003」「透過B.003」「金属.001」「DefaultMaterial」等の名前があり、実際の.mat名と一致しないものもある。

共有フォルダーの検索だけを増やしたり、.001等を機械的に削除したりすると、色違い・別用途を誤適用する可能性がある。

## 3. 選択Prefabの材質差分18箇所が未反映

あのめあMA.prefab内にm_Materials.Array.data[n]の差し替えが18箇所ある。例：

- メインA：013007931a6af3e4c9b2d5730e47138a
- メインB：ebfc53baa0cc69b4b91f0ba86bcd1383
- 透過a：19f04d7e14f6ca540886ecda536a63db
- 透過B：3fd4090fa6ebe8549b800133d6ae99b6
- ファー：8e76e1287f8f78342a0ab3b94253682c

現行のFBXインスタンス表示は元モデルの初期形状・材質名を使い、こうした差分を反映しない。次の材質対応では、差分の対象Renderer fileIDを元モデルのノード・サブメッシュへ対応付け、選択Prefabのスロット差し替えを優先する必要がある。

## 4. 実際の参照先と画像寸法

選択依存範囲に保存された材質参照は48箇所、異なる材質GUIDは14件。14件とも.matがパッケージ内に存在し、それぞれの_MainTexもすべて存在した。

衣装の主画像「デフォルトA.png」「デフォルトB.png」は4,096×4,096で現行画像上限内。ペットのpet.pngも2,048×2,048で上限内。

一方、以下の別要因がある。

- 特殊効果用の保存プロパティには、未同梱の画像GUIDが7種類・19参照ある。_DistNormalMap、_DistortTex、_FadeTex、_FurNoiseMask、_MaskTex、_Shape1MaskTex、_Shape2DistortTex、_Shape3DistortTex、_ShapeDistortTex、_VertOffsetTex等。これらが実際に使われるかはShaderと設定に依存する。現行簡易描画は主画像・透過マスクを中心に扱うため、これらの不足だけで現在の単色表示全体を説明することはできない。
- パーティクル用のお菓子.pngとハート.pngは各5,669×5,669（32,137,561画素）。ファイルは存在するが、現行の16,777,216画素上限を超える。対応するなら、上限を単純に増やすよりも、縮小復号・安全な画素予算の扱いを検討する必要がある。

## 5. 出力が重複していた理由

元FBXで材質補完に失敗すると、一度「材質名：補完の上限超過」という注記が作られる。その後Prefabの内部用GUID付き材質名を解決しようとして、同じ失敗に対し「GUID_材質名：対応する.matを解決できません」が追加されていた。

v0.9.2は元の具体的な理由がある場合、この二次的な汎用メッセージを重複表示しない。キャッシュに残った旧注記も表示時に整理する。

## v0.9.2での表示確認

- モデルは頂点29,192、三角形44,886、Mesh 27、依存材質15、Bone 323で再現。
- パッケージ解析の既存427件を維持し、選択モデルの参照警告15件と制限情報2件を同じ警告欄へ統合。
- 警告欄は自動展開せず、プレビュー内は統計・操作案内のみ。警告欄のサイズ変更・別ウィンドウ表示も維持。
- キャッシュ再表示でも注記を警告欄へ復元し、プレビュー画像を文字で覆わない。
- モデルや画像の表示失敗、キャッシュ保存失敗の詳細も警告欄へ集約する。

## 次の参照対応案

1. 選択Prefabの依存グラフを作り、無関係なバリエーション414件を一括推定の対象にしない。
2. FBXインスタンスのRenderer/fileID・スロット対応と、選択Prefabの材質差し替えを実装する。
3. 名前推測より明示GUIDを優先し、Material/Mat等の共有配置を扱う。
4. 非表示オブジェクト、Pet、パーティクル、Prefab上の配置差分を区別する。現在の位置・表示状態の相違は、材質だけの問題ではない。
5. 特殊Shader依存や大きい画像は、上記の主参照対応と分けて検証する。

## 検証物

- catalog.json：パッケージ構成
- selected-edges.json / selected-materials.json：選択依存範囲
- texture-reference-detail.json / primary-texture-dimensions.json：画像参照と寸法
- reference-summary.json：集計
- runtime-findings.json：本番表示経路での警告
- v0.9.2-preview.png / v0.9.2-cached.png / v0.9.2-warnings.png：確認画面

すべてartifacts/fuwatto-analysis/に保持し、モデル・画像・抽出Prefabを配布物へ含めない。任意の再検証はUIテストに--fuwatto-analysisを渡す。Rドライブ上の指定ファイルが必要。

## v0.10.0 参照処理拡張（2026-11-16）

選択Prefabの依存グラフを層ごとに一括読み込みし、関係のない414件全体を補完解析しない方式を実装。依存32種類・合計64 MiB、深さ8・訪問64、モデル16のガードを設けた。裸のFBXの全Prefab逆引きは従来どおり保守的な32件上限を維持する。

Unity importerチームが説明するType/identifierのXXH64生成規則と、入れ子PrefabのXOR合成を独立実装した。数値IDが一致する対象だけにm_Materials.Array.data / Array.size、Renderer.m_Enabled、GameObject.m_IsActiveを適用する。Variantの上位差分が下位の材質を上書きする。strippedの明示参照も引き継ぐ。明示的なnull材質を区別し、勝手に元材質へ戻さない。日本語YAML名のUnicodeエスケープも復号する。

主FBXの18箇所の材質差分とPetの材質を反映し、27メッシュ・44,886三角形の9材質をすべて解決。旧15件の材質補完エラーは解消した。一方、hane.fbxのRenderer ID -1795936056128106804に対する4スロット分の差分は、現在読み取ったモデルのRenderer IDと一致しない。同じIDについて警告を1件にまとめ、推測割り当ては行わない。有効なエッグ・ウイングのRenderer IDに対する差分は解決する。パーティクルなど静止メッシュを持たない既知GameObjectへの有効状態変更は、描画する形状がないため警告にしない。

材質名による探索はMaterials / Material / Matを同一距離の候補として扱う。競合候補は自動選択しない。配置変更・Bone姿勢・BlendShape・追加/削除Component・Unity専用Shader・ParticleSystemは今回の再現範囲に含めない。専用画像の欠落・画素上限も変更していない。

識別子の根拠（Unity importer担当者の説明。Unityの内部実装はバージョンにより変わり得るため、不一致は警告で明示する）：
- https://discussions.unity.com/t/fbx-submesh-fileids/803882
- https://discussions.unity.com/t/how-is-fileid-generated-for-m_correspondingsourceobject/754235

System.IO.Hashing 10.0.0をMITで使用。テスト用データ・抽出ファイル・検証画像はartifacts内のみで、配布ZIPへ含めない。
