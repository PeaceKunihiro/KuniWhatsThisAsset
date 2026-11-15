# モデル表示機能の検討

調査日：2026-11-15。現在版：v0.5.0。以下は実装案であり、ライブラリ導入・実機PoC・性能計測は未実施。

## 結論

第一候補は **SharpAssimp（モデル解析）＋ HelixToolkit.Wpf.SharpDX（描画）**。現在のWPF UIと左ツリー／右プレビュー配置を維持し、FBX・OBJの静止モデル表示から始める。FBX解析互換性に問題が出た場合はufbxを比較評価する。モデル形式のパーサーと描画エンジンは独自実装しない。

採用はPoC後に確定する。.NET 10のビルドと自己完結型Windows x64成果物、実アバターモデルの互換性、外部ファイルアクセス制御を確認する。NuGetの互換性表示は本アプリでの動作保証とは区別する。安定版を固定し、プレリリースを既定にしない。

## 候補比較

| 構成 | 長所 | 検証すべき点 | 判断 |
| --- | --- | --- | --- |
| HelixToolkit.Wpf.SharpDX ＋ SharpAssimp | WPF上のDirectX 11描画と、多形式の解析を分担できる | ネイティブDLLの同梱、GPU互換性、依存関係、外部参照の制御 | 第一候補 |
| WPF標準Viewport3D ＋ SharpAssimp | 既存WPFの描画機能で静止形状を表示できる | 大きいアバターや多数の材質、半透明、将来のスキニングの負荷 | 簡易PoC・比較用 |
| HelixToolkit ＋ ufbx | FBX・OBJ、スキニング・BlendShape等を扱うパーサー候補 | Cライブラリのビルド、C#との橋渡し、描画用データへの変換 | FBX互換性の比較候補 |

HelixToolkitはMIT。SharpAssimpは管理コードがMIT、ネイティブAssimpが3-Clause BSD。ufbxはMITまたはPublic Domainの選択式で、採用する場合はMITを選ぶ方針。配布時は固定した版の依存ライセンス・通知と実際に同梱するDLLを一覧化する。SharpAssimp同梱ネイティブ版と上流Assimpの最新版を同一視しない。テスト用モデルの利用条件も個別に確認する。

## 初回実装の範囲案

### 段階1：形状を確実に確認する

- FBX（Binary / ASCII）・OBJをツリーで選択し、既存の右領域に表示する。
- 静止状態を表示。スキン付きモデルはバインド姿勢と変換の整合性を確認する。Tポーズになるとは断定しない。
- マウスによる回転・平行移動・ホイールズーム、全体表示、正面復帰を提供する。
- 基本色または共通の簡易材質で表示し、表裏・法線・透過の基本動作を確認する。
- 頂点数、三角形数、Mesh数、Material数、Bone数、Skinned Mesh有無を表示する。
- 選択変更時の中断、古い解析結果の破棄、表示失敗時のパッシブな通知に対応する。

統計の定義も固定する。三角形数は三角形化後、頂点数はインポートされたメッシュの頂点数とする。Meshの定義とインスタンス数は区別する。マテリアルはモデル内の材質数・スロットであり、Unityの.matファイル数とは一致しない。Bone数を単なるノード総数として表示しない。Humanoid判定はこの段階では行わない。

### 段階2：テクスチャと簡易マテリアル

- FBX埋め込み画像、OBJのMTL、パッケージ内の外部画像を解決する。
- 最初はベースカラーと基本的な透過に対応。欠落画像は共通材質にフォールバックする。
- 仮想パスとGUIDの索引を構築し、.metaのマテリアル再割り当てや.matの画像参照を静的に解決する。
- 同名画像の曖昧な自動割り当ては避ける。自動検索する場合も同じパッケージ内に限定し、推定を明示する。
- TGA/DDS等の画像形式は別途デコーダーとライセンスを検証する。現在の画像表示機能の対応範囲を流用できる部分から始める。

Unityのマテリアル再割り当てによって、FBXに含まれる材質とは異なる外部材質が使われることがある。FBXを開くだけでPrefabの完成状態を再現できるとは扱わない。lilToon等の独自Shader、衣装合成、Prefab構成の再現、Humanoid判定、アニメーション・表情再生は後続の独立課題とする。

## UI案

- 左のツリーを維持し、選択したFBX/OBJを右の広い領域へ表示する。
- モデル名・統計は小さなオーバーレイまたは必要時だけ開く情報欄にまとめる。パッケージ内パス一覧を右側に復活させない。
- 回転等はマウス中心とし、常設操作は全体表示・正面復帰程度に抑える。
- 配色・アプリアイコンを維持。警告は既存のパッシブ通知と分離ウィンドウを利用する。

## 読み込みと安全性

1. 読み込み済みパッケージのGUID／仮想パス索引から対象モデルと関連データを取得する。
2. モデル解析は別プロセスで行う。単に別プロセスにするだけで完全なサンドボックスになるとは扱わず、権限・ファイルアクセス・通信制限の実装を別途検証する。
3. 解析ライブラリの既定ファイル探索を無効化し、許可したパッケージ内データだけを渡す。外部URL・UNC・ローカルPC上の任意ファイルにフォールバックしない。相対参照は仮想パス上で正規化し、パッケージ外への移動を拒否する。
4. アプリが配布する解析用DLLのみをロードし、パッケージ内のDLL・C#・Shaderを実行しない。描画にはアプリ側で用意した固定の材質・Shaderを使う。
5. UIには頂点・索引・変換・材質等のデータを返し、件数、バッファ長、索引範囲、NaN/Infinity、階層深度をUI側でも検証する。任意の型を復元するシリアライズ方式を使わない。
6. 解析プロセスの終了・タイムアウト・キャンセルでUIが固まらないことを確認する。重い処理はUIスレッドに置かない。

既存のパッケージ読み込み上限解除の承認は引き継ぐ一方、モデル解析・GPU表示の予算は別に設計する。具体的なメモリ・頂点数等の値と上限到達時の動作は性能検証後に決め、ユーザーへ明示する。プレビューだけが制限される場合も内容物一覧は保持する。

現在のReadAssetBytesは1ファイル取得ごとにパッケージ全体を再走査するため、多数のテクスチャがあるモデルでは繰り返し走査が増える。関連アセットの一括取得や、読み込み単位での上限付きキャッシュを検討する。圧縮済みTAR内の位置を単純なランダムアクセス索引として扱わない。

## PoCの合格条件

- 自作または利用条件を確認したOBJ、Binary/ASCII FBX、スキン付きアバター、複数メッシュ、負スケール等のモデルが正常に表示される。
- 形状の向き、単位、ノード変換、法線、三角形化、バインド姿勢と統計の定義を検証する。
- アーカイブ内の埋め込み／外部テクスチャ、MTL、欠落・曖昧参照を確認する。
- 悪意ある外部パス、壊れたモデル、異常な索引やサイズで、任意ファイル読取・外部通信・UI停止が起きないことを検証する。
- 選択連打・キャンセル・モデル切り替えで古い結果が混入せず、CPU/GPUメモリが解放される。
- 現在のWindows x64自己完結型配布でネイティブDLLが解決され、利用条件・通知が揃う。

実装時は新機能追加として、その時点の次のマイナーバージョンを候補とする。v0.6.0は後続のライセンス表示機能に割り当て済み。今回の検討だけではv0.5.0を更新せず、StableのZIPも変更しない。

## 一次資料

- [HelixToolkit公式](https://github.com/helix-toolkit/helix-toolkit)
- [HelixToolkit.Wpf.SharpDXの公開パッケージ・依存関係](https://www.nuget.org/packages/HelixToolkit.Wpf.SharpDX/)
- [SharpAssimp公式](https://github.com/JeremyAnsel/SharpAssimp)
- [SharpAssimpの配布・ライセンス情報](https://www.nuget.org/packages/SharpAssimp/)
- [Assimp対応形式](https://github.com/assimp/assimp/blob/master/doc/Fileformats.md)
- [Assimpライセンス](https://github.com/assimp/assimp/blob/master/LICENSE)
- [ufbx機能](https://github.com/ufbx/ufbx/blob/master/README.md)
- [ufbxライセンス](https://github.com/ufbx/ufbx/blob/master/LICENSE)
- [WPF 3Dの概要](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/3-d-graphics-overview)
- [WPF 3Dの性能上の注意点](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/maximize-wpf-3d-performance)
- [Unity公式：モデルのマテリアル設定](https://docs.unity.com/en-us/engine/6000.6/manual/assets-and-media/asset-types/models/importing/fbximporter-materials)

