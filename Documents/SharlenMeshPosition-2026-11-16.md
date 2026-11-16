# Sharlenの髪メッシュ位置ずれ（2026-11-16 / v0.10.1）

対象：ユーザー提供のR:/Sharlen.unitypackage。解析用の抽出FBX・Prefab・画像はartifacts/sharlen-analysis内だけへ保存し、配布物には含めない。

## 原因

Sharlen.fbxではBodyがルート直下、HairがBodyの子であり、双方のLcl RotationにX=-90.000009°が記録されている。従来のModelImporterはこの親子の初期変換を累積し、Hairの全体回転が約-180°になっていた。

一方、FBXのBindPoseにあるModel ID 54396324（Body）と417723241（Hair）の全体行列は、双方ともX=-90°である。初期ノード階層とバインド時の配置が一致していない。Assimpの出力Bone.OffsetMatrixもノードの累積全体変換から生成されるため、OffsetMatrixとBone全体変換の積だけを使用しても、このデータのずれは解消しなかった。PrefabにはHairの局所回転をidentityへ戻す差分も存在するが、今回の修正はPrefab差分の全面対応ではなく、元FBXの静止バインド姿勢を利用する。

## 修正

- バイナリFBXのObjects/ModelのIDと名前、BindPose/PoseNodeのNode IDとMatrixだけを読み取る。
- 7400系の32bit・7500系以降の64bitノードヘッダー、非圧縮/128byteへ展開されるzlib圧縮行列を扱う。範囲・深さ128・ノード20万・行列値・圧縮長を制限する。
- 一意に対応するスキン付きメッシュは、BindPoseの全体行列とインポート後のルート座標変換を一度だけ適用する。法線の逆転置・反射時の面方向も同じ行列に合わせる。
- 重複名・矛盾した複数姿勢・特異行列は採用しない。Geometric変換の補助ノードを含む経路は従来の変換を維持する。
- 通常メッシュ、ASCII FBX、姿勢情報のないFBXは従来のノード変換を維持。バインド解析に失敗した場合もノード変換を維持し、警告欄へ理由を表示する。
- アニメーションやBone変形は再生しない。Prefab配置差分の完全再現も今回の対象外。
- v0.10.1-1へキャッシュ改訂番号を更新し、位置がずれた古い写真を再利用しない。

## 検証

従来Hairの範囲：Y=-0.1945～0.1318、Z=-1.6575～-1.1790。修正後：Y=1.1790～1.6575、Z=-0.1945～0.1318。Headの高さは約1.3725で、顔のY範囲は1.3437～1.5817。正面・側面画像でも髪が頭部へ戻ることを確認した。

Sharlen.fbx、Sharlen.prefab、Sharlen FT.prefab、Sharlen_alt.prefab、Sharlen_alt2.prefabの5経路を確認。Core 53件とWPF UI、7400/7500・圧縮/非圧縮・行列方向・矛盾/重複・不正オフセットのテストが成功。Akyo PC/Quest・ネコメイド6経路、Fuzzy/LuchikaのFBXとPrefabを回帰確認。FUWATTOの旧Rドライブ入力は現在存在せず、今回の版での再表示は未実施。

参考（元の仕様・実装を確認。FBXバインド読み取り部分は独自実装）：
- [Assimp aiBoneの座標系](https://github.com/assimp/assimp/blob/master/include/assimp/mesh.h)
- [Assimp 6.0.2 FBXConverterのConvertCluster](https://github.com/assimp/assimp/blob/v6.0.2/code/AssetLib/FBX/FBXConverter.cpp)
