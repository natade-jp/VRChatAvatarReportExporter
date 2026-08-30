# VRChat Avatar Report Exporter

VRM 0.x / VRM 1.0 / XAvatarなど、異なる経路でVRChat向けに変換したアバターを比較・調査するためのUnity Editor拡張です。

アバターのHierarchy、Mesh、Bone、BlendShape、Material、Texture、Animator、VRC Avatar Descriptor、VRC PhysBoneなどの情報をJSON形式で出力します。

## インストール

GitHubからリポジトリをダウンロードします。

https://github.com/natade-jp/VRChatAvatarReportExporter

1. GitHubのリポジトリを開く
2. `Code` → `Download ZIP` を選択
3. ダウンロードしたZIPを展開
4. `Editor/AvatarReportExporter.cs` をUnityプロジェクトの任意の `Editor` フォルダーへコピー

例:

```text
Assets/
└─ AvatarReportExporter/
   └─ Editor/
      └─ AvatarReportExporter.cs
```

VRChat SDKへのコンパイル時依存はありません。

VRChat固有Componentについても、SDKの型を直接参照せず `SerializedObject` / `SerializedProperty` を利用して設定値を取得します。

## 使い方

1. UnityのHierarchyで比較したいアバター全体を含む最上位のGameObjectを選択
2. `Tools > VRChat > Export Avatar Report` を実行
3. JSONの保存先を選択
4. `<AvatarName>-avatar-report.json` が出力されます

たとえば、次のようなHierarchyの場合は `myavatar` を選択します。

```text
myavatar
├─ Root
│  ├─ VRC Avatar Descriptor
│  ├─ Hair
│  └─ ...
├─ secondary
│  └─ VRC PhysBone
└─ Body
   ├─ Skinned Mesh Renderer
   ├─ Materials
   └─ ...
```

`Root` だけを選択すると、兄弟階層にある `secondary` や `Body` は出力対象になりません。

1プロジェクト1アバターでの使用を想定しており、ツール内にレポート同士を比較する機能はありません。

## 出力内容

### Summary

アバター全体の主要な情報を集計します。

- GameObject数
- Component数
- SkinnedMeshRenderer数
- MeshRenderer数
- Unique Mesh数
- Unique Meshの頂点数・三角形数
- 使用BoneのUnique数
- BlendShape数
- Material Slot数
- Unique Material数
- Unique Texture数
- VRC PhysBone数
- VRC PhysBone Collider数
- VRC Contact Sender数
- VRC Contact Receiver数
- Component型ごとの件数

### Hierarchy

アバターのGameObject構造を再帰的に出力します。

- GameObject名
- RootからのHierarchy Path
- Active状態
- Tag
- Layer
- Local Position
- Local Rotation
- Local Scale
- Component型一覧
- 子GameObject

### SkinnedMeshRenderer

各SkinnedMeshRendererについて以下を出力します。

- Mesh
- 頂点数
- 三角形数
- SubMesh数
- Root Bone
- Bone一覧
- Material一覧
- Local Bounds
- BlendShape

### BlendShape

各BlendShapeについて以下を出力します。

- Index
- 名前
- 現在のWeight
- Frame数

各Frameについて以下を出力します。

- Frame Weight
- 影響頂点数
- 影響法線数
- 影響接線数
- 最大頂点移動量
- 影響頂点の平均移動量
- 最大法線差分
- 最大接線差分
- 法線差分の有無
- 接線差分の有無

頂点ごとのDelta配列そのものは、JSONサイズが非常に大きくなるため出力しません。

### Material

各Materialについて以下を出力します。

- Material名
- Asset Path
- Shader名
- Shader Asset Path
- Render Queue
- GPU Instancing
- Double Sided GI
- GI Flags
- Shader Keywords
- Shaderが公開しているProperty
    - Float
    - Range
    - Color
    - Vector
    - Texture
    - Texture Scale
    - Texture Offset

MToon、MToon10、lilToonなど異なるShaderを使用した場合の設定差の調査にも利用できます。

### Texture

Materialから参照されているTextureについて以下を出力します。

- Asset Path
- Width / Height
- Graphics Format
- Mipmap数
- Wrap Mode
- Filter Mode
- Aniso Level

TextureImporterについても以下のような設定を出力します。

- Texture Type
- Texture Shape
- sRGB
- Alpha
- Mipmap
- Streaming Mipmaps
- Streaming Mipmaps Priority
- Max Texture Size
- Compression
- Compression Quality
- Crunch Compression
- NPOT Scale
- Read/Write
- その他の主要なImporter設定

### Animator

各Animatorについて以下を出力します。

- Animator設定
- Avatar参照
- RuntimeAnimatorController
- AnimatorController Parameters
- Layers
- States
- Motion参照
- Write Defaults
- Avatar Mask

### VRC Avatar Descriptor

VRC Avatar Descriptorについては、汎用的なSerializedPropertyの出力に加えて、比較しやすい専用セクションを出力します。

#### View Position

Avatar Descriptorに設定されているView Positionを出力します。

#### Lip Sync

- Lip Sync Mode
- Viseme Skinned Mesh
- Viseme BlendShape割り当て
    - sil
    - PP
    - FF
    - TH
    - DD
    - kk
    - CH
    - SS
    - nn
    - RR
    - aa
    - E
    - ih
    - oh
    - ou

- Jaw Bone
- Jaw Open
- Jaw Closed
- Mouth Open BlendShape

VRM 0.x / VRM 1.0 / XAvatarなど、変換方法によって生成されるLip Sync設定に違いがあるか確認できます。

#### Eye Look

- Eye LookのEnable状態
- `customEyeLookSettings` 以下の設定
- Eye Transform
- Rotation State
- Eyelid関連設定

内部設定はSerializedPropertyを再帰的に取得します。

#### Playable Layers

- Customize状態
- Base
- Additive
- Gesture
- Action
- FX
- Sitting
- T-Pose
- IK PoseなどのSpecial Layers
- Default状態
- Animator Controller参照

Animator Controllerが設定されている場合は、Controller内部についても以下を出力します。

- Parameters
- Layers
- States
- Motion
- Write Defaults
- Avatar Mask

#### Expressions

- Customize状態
- Expressions Menu参照
- Expression Parameters参照

Expressions Menu / Expression Parametersについては、Assetへの参照だけでなく内部のSerializedPropertyも再帰的に出力します。

#### Raw SerializedProperty

VRC Avatar DescriptorのSerializedPropertyを、配列や入れ子を含めて階層構造を保ったまま出力します。

専用セクションで取得していない設定が存在する場合も、Raw SerializedPropertyから確認できます。

### その他のComponent

Transform / Renderer / Animatorなど専用セクションで扱うものを除き、ComponentのSerializedPropertyを汎用的に出力します。

そのため、プロジェクト内に存在する以下のようなVRChat Componentについても、SDKの型へ直接依存せず設定値を比較できます。

- VRC PhysBone
- VRC PhysBone Collider
- VRC Contact Sender
- VRC Contact Receiver
- VRC Constraints
- Pipeline Manager
- その他SDKのSerializableな設定

ObjectReferenceは、可能な場合はHierarchy PathまたはAsset Pathとして出力します。

Instance IDのようなプロジェクトごとに変化しやすい値は、比較用の識別情報として使用しません。

## Environment

レポートを作成したUnity環境について以下を出力します。

- Unity Version
- Platform
- Active Build Target
- Unity Package ManagerでロードされているPackage
    - Package名
    - Version
    - Source

VRM 0.x / VRM 1.0 / XAvatarなどを比較する際に、UniVRM、VRChat SDK、Converterなどのバージョン差も記録できます。

## 比較での利用例

同じアバターを異なる方法でVRChat向けに変換し、それぞれのJSONを比較することで、変換方法による違いを調査できます。

たとえば以下のような比較を想定しています。

```text
VRoid Studio
├─ VRM 0.x → VRM Converter for VRChat
├─ VRM 1.0 → VRM1.0 to VRChat Converter
└─ XAvatar → XWear Packager
```

比較できる項目の例:

- Hierarchy構造
- Bone構造
- Bone数
- Mesh構成
- 頂点数・三角形数
- BlendShape構成
- Material数
- Shader
- Shader Property
- Texture
- Animator
- Lip Sync
- Eye Look
- Playable Layers
- Expressions
- VRC PhysBone
- その他VRChat Component

## 注意

- JSONは比較・調査向けで、Unity Objectを完全に復元するための形式ではありません。
- SerializedPropertyの内部表現はComponentやSDKのバージョンによって変化する場合があります。
- MaterialのShader PropertyはShaderが公開しているPropertyを列挙します。
- BlendShape Frame解析は頂点数に比例して処理量が増えます。BlendShapeが非常に多いモデルでは出力に時間がかかる場合があります。
- 出力結果にはConverterやSDKが自動生成した設定も含まれるため、変換方式を比較する場合はUnityやPackageのバージョンなど、できるだけ条件を揃えることを推奨します。
