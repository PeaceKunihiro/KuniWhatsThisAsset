# 依存コンポーネント

KuniWhatsThisAsset v0.10.0は.NET 10 / WPFおよびSharpAssimp 6.0.12を使用します。描画は標準WPF Viewport3Dを使用します。Unityモデル識別子のXXH64計算にSystem.IO.Hashing 10.0.0（MIT）を使用します。MIT原文とNuGetパッケージの第三者通知を同梱し、Help→ライセンスに表示します。

SharpAssimpの管理コードはMIT、同梱Assimpネイティブ部分はBSD 3-Clauseです。パッケージに含まれるPoly2Tri等の通知も含めた原文を`licenses/SharpAssimp-LICENSE.txt`へ同梱し、Help→ライセンスで表示します。ソース原本はNuGet 6.0.12のLICENSE.txtです。

- [SharpAssimp公式](https://github.com/JeremyAnsel/SharpAssimp)
- [SharpAssimp 6.0.12](https://www.nuget.org/packages/SharpAssimp/6.0.12)

同梱`Native/assimp.dll`の動作にはMicrosoft Visual C++ v14 x64ランタイムが必要です。VC++ランタイムは本ZIPへ同梱せず、Microsoft公式インストーラーを案内します。テスト用アバター等の第三者アセットは本配布物に含めません。

自己完結型のWindows x64成果物にはMicrosoft.NETCore.AppとMicrosoft.WindowsDesktop.Appが含まれます。発行フォルダーの`licenses/`に、それぞれのランタイムパックに含まれるライセンス、および.NET Coreの第三者通知を同梱しています。配布物をコピーする際は、これらのファイルも保持してください。

- [.NETライセンス情報](https://github.com/dotnet/core/blob/main/license-information.md)
- [.NET Runtime](https://github.com/dotnet/runtime)
- [WPF](https://github.com/dotnet/wpf)

本プロジェクト自体の公開ライセンスは未定です。




ライセンスはアプリのHelp→ライセンスからも確認できます。文書の原本はsrc/KuniWhatsThisAsset.App/Licenses/に保持し、発行先へ自動コピーします。





- [System.IO.Hashing 10.0.0](https://www.nuget.org/packages/System.IO.Hashing/10.0.0)
- [System.IO.Hashing / .NET Runtimeライセンス](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT)
