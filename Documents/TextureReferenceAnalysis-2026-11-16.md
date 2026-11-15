# テクスチャ参照障害の解析（2026-11-16）

対象実装：KuniWhatsThisAsset v0.9.0。ユーザー提供の5000porynecomaid、キタキツネAkyo v1.01を使用。アプリ本体やStableの配布物は変更せず、解析用プローブをartifacts/texture-analysis/Probeへ作成した。パッケージと同梱文書は入力データとして扱い、スクリプトや外部DLLは実行していない。モデルの解析には本体同梱のAssimp workerを使用した。

## 結論

両件ともPNG画像がパッケージ内にあり、マテリアルからのGUID参照も有効。画像はともに1024×1024で、現行デコーダーの上限内。参照先を診断用に明示した描画ではテクスチャを反映できた。主因は画像デコードやUVの欠損ではなく、モデルからUnityマテリアルを探す処理にある。

## 再現結果

既存のレンダリングキャッシュを使わず、現行ModelMaterials.LoadとPrefabPreviewBuilderを呼んで確認した。

| 対象 | 三角形 | 現行のテクスチャ反映 | 診断用の明示参照 |
| --- | ---: | --- | --- |
| ネコメイド FBX | 4,992 | なし | 成功 |
| ネコメイド 設定済Prefab | 4,992 | 成功 | 成功 |
| Akyo PC FBX | 4,045 | なし | 成功 |
| Akyo PC Prefab | 4,045 | なし | 成功 |
| Akyo Quest FBX | 4,045 | なし | 成功 |
| Akyo Quest Prefab | 4,045 | なし | 成功 |

診断用の明示参照は、元ファイルを書き換えず、読み込み時のメタデータをメモリ上で与えたもの。アプリに修正を適用した結果ではない。

## 1. キタキツネAkyo：兄弟Materialsフォルダーを検索できていない

パッケージ内の構成：

```text
Assets/KTNTK_Akyo/
  FBX/KTNTK_Akyo.fbx
  FBX/KTNTK_Akyo_Quest.fbx
  Materials/Main.mat
  Materials/Main_Quest.mat
  Textures/Main.png
  KTNTK_Akyo.prefab
  KTNTK_Akyo_Quest.prefab
```

FBXから取得した材質名はPC版がMain、Quest版がMain_Questで、対応する.mat名と一致する。しかし現行処理はモデルのフォルダー以下だけをStartsWithで検索するため、FBX/配下から兄弟のMaterials/へ進めない。

両FBXの.metaはexternalObjectsが空で、明示GUID割り当てからも補完できない。materialSearchの設定値は記録されているが、現行コードはその探索規則を実装していない。Unityには親方向のMaterialsフォルダーを探すRecursiveUpの仕様がある。

PrefabはFBXインスタンス形式。PrefabPreviewBuilderにも同じ配下限定の検索があり、材質GUIDを解決できず、GUIDを付けた内部材質名だけを下流へ渡す。後段のModelMaterialsも実在のMain.mat/Main_Quest.matを結び付けられない。FBXとPrefabの両経路に共通する問題。

実際の参照先：

- Main.mat：ee4ee302e7bbc02498fb45bfa3dea393
- Main_Quest.mat：bd03da30c3279d445b862ff699f38898
- 両者の_MainTex：2035ee811202c6448b5560078daba29b → Textures/Main.png

Quest FBX内にはTextures/Main_Quest.pngという古い画像名も残るが、現在の.matはMain.pngを正しく参照している。FBX内の画像ファイル名だけで解決しようとすると、ここでも誤る。

## 2. 5000ポリネコメイド：FBXの材質名と.mat名が異なる

FBX内の材質名はlowだが、パッケージ内のマテリアルはMaterials/5000porynecomaid.mat。FBX.metaのexternalObjectsは空であり、名前一致による補完も成立しない。

FBX内の画像参照名はネコメイドlow20.png（Unicodeの結合文字を含む）で、同梱PNGの5000necomaid.pngとも異なる。ただし、名前が日本語であることを主因とする証拠はない。モデル・UVは読み込みでき、明示GUID参照では表示できている。

設定済PrefabはBodyのRendererから材質GUIDを直接指定するため、現行実装でもテクスチャ付きで表示できた。

```text
5000porynecomaid設定済.prefab
  Body / Renderer
    material: 4e655fc59f4bbe641b13eae812b4f7d6
      Materials/5000porynecomaid.mat
        _MainTex: 69c4134daab102e40945ba15145e0bf5
          5000necomaid.png
```

当面は、FBX単体ではなく設定済Prefabを選択することで表示可能。FBX単体の自動補完では、参照するPrefabからMesh/材質スロットの割り当てを逆引きし、割り当てが一意な場合だけ使う方法が考えられる。候補が1個という理由だけでパッケージ全体の画像を強制適用する方法は、別アバターや色違いを取り違えるため避ける。

## 修正方針

1. FBXとPrefabのマテリアル解決を共通化する。明示GUID参照を優先する。
2. モデル.metaのmaterialSearch/materialNameを考慮し、親方向のMaterialsフォルダー等をパッケージ内で探索する。PC/Questの名前を区別し、曖昧な候補は自動選択しない。
3. 名前が異なる旧アセットでは、同じFBXを参照するPrefabのRenderer/スロット割り当てを参照する。複数Prefabで割り当てが異なる場合は、選択Prefabを優先するか未解決として説明する。
4. 解決できなかった材質はモデル領域へ理由を出す。現状は単色へのフォールバックが、単にテクスチャがないモデルのように見える。
5. 修正時はレンダリングキャッシュの改訂番号を更新する。パッケージの内容が同じでも、現行実装で作った単色写真が残っている場合がある。

今回の原因を修正する変更は、既存との互換性を維持する不具合修正としてv0.9.1が適切。

## 該当実装

- src/KuniWhatsThisAsset.App/ModelMaterials.cs：モデル配下・同名.matだけを検索する処理。
- src/KuniWhatsThisAsset.App/PrefabPreview.cs：元FBXの材質解決に同じ検索を重複実装する処理。
- src/KuniWhatsThisAsset.Core/RenderCache.cs：描画改訂番号を含むキャッシュキー。

## 検証物

生データ、全6経路のJSON、キャッシュを介さない現行描画と明示参照描画、診断用プローブはartifacts/texture-analysis/に保持する。これらはGit管理・配布対象外。

## 参考

- [Akyo商品ページ](https://kitsune-tsuki.booth.pm/items/5615337)：ユーザー提供資料とパッケージを優先して照合。5000ポリネコメイドは商品ページ資料なし。
- [Unity公式：ModelImporterMaterialSearch.RecursiveUp](https://docs.unity3d.com/ja/6000.0/ScriptReference/ModelImporterMaterialSearch.RecursiveUp.html)
- [Unity公式：ModelImporterMaterialName](https://docs.unity3d.com/ja/6000.0/ScriptReference/ModelImporterMaterialName.html)

## v0.9.1での対応（2026-11-16）

上記修正方針を実装。MaterialResolverを共通化し、親探索・命名・明示GUID・Prefabスロットの一意補完に対応した。本番の解決処理に診断用の強制割り当てを与えず、表の全6経路でテクスチャ解決と描画が成功。PC/Questは別の正しい材質GUIDを選ぶことも検証した。描画改訂番号を更新し、単色の旧キャッシュを再利用しない。

修正後の検証物はartifacts/texture-fixed-v0.9.1/。任意検証コマンドはUIテストの--texture-models。元のv0.9.0解析結果は当時の記録として保持する。
