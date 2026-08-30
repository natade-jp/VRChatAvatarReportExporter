using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Natade.AvatarReport
{
    /// <summary>
    /// 選択したアバター以下の比較用情報をJSONへ出力するEditor拡張
    /// </summary>
    public static class AvatarReportExporter
    {
        private const string ReportFormatVersion = "1.1.6";
        private const float DeltaEpsilon = 0.0000001f;

        /// <summary>
        /// 選択したGameObjectをルートとしてアバター情報をJSONへ出力
        /// </summary>
        [MenuItem("Tools/VRChat/Export Avatar Report")]
        public static void Export()
        {
            var root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog(
                    "Avatar Report Exporter",
                    "HierarchyでアバターのRoot GameObjectを選択してください。",
                    "OK");
                return;
            }

            try
            {
                var report = BuildReport(root);
                var json = JsonUtility.ToJson(report, true);

                var defaultName = SanitizeFileName(root.name) + "-avatar-report.json";
                var path = EditorUtility.SaveFilePanel(
                    "Avatar Report JSONを保存",
                    Application.dataPath,
                    defaultName,
                    "json");

                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
                Debug.Log($"Avatar Reportを出力しました: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "Avatar Report Exporter",
                    "レポートの作成中にエラーが発生しました。\nConsoleを確認してください。",
                    "OK");
            }
        }

        /// <summary>
        /// アバターレポート出力メニューを有効化できるか確認
        /// </summary>
        /// <returns>GameObjectが選択されている場合はtrue、それ以外はfalse</returns>
        [MenuItem("Tools/VRChat/Export Avatar Report", true)]
        public static bool ValidateExport()
        {
            return Selection.activeGameObject != null;
        }

        /// <summary>
        /// 指定したルートGameObject以下を走査してアバター全体のレポートを構築
        /// </summary>
        /// <param name="root">アバター全体を含む最上位のGameObject</param>
        /// <returns>Hierarchy、Mesh、Material、Texture、Animator、VRChat設定などを格納したアバターレポート</returns>
        private static AvatarReport BuildReport(GameObject root)
        {
            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            var allComponents = root.GetComponentsInChildren<Component>(true);

            var report = new AvatarReport
            {
                reportFormatVersion = ReportFormatVersion,
                avatarName = root.name,
                rootScenePath = GetHierarchyPath(root.transform, root.transform),
                environment = BuildEnvironment(),
                hierarchy = BuildHierarchy(root.transform, root.transform),
                skinnedMeshRenderers = BuildSkinnedMeshReports(root),
                staticMeshRenderers = BuildStaticMeshReports(root),
                animators = BuildAnimatorReports(root),
                vrchat = BuildVRChatReport(root),
                components = BuildComponentReports(root)
            };

            report.materials = BuildMaterialReports(report, root);
            report.textures = BuildTextureReports(report.materials);
            report.summary = BuildSummary(
                root,
                allTransforms,
                allComponents,
                report.skinnedMeshRenderers,
                report.staticMeshRenderers,
                report.materials,
                report.textures);

            return report;
        }

        /// <summary>
        /// Unity EditorとUnity Package Managerの環境情報を取得
        /// </summary>
        /// <returns>Unityバージョン、Build Target、導入Package一覧を格納した環境情報</returns>
        private static EnvironmentReport BuildEnvironment()
        {
            var result = new EnvironmentReport
            {
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString()
            };

            try
            {
                result.packages = PackageInfo.GetAllRegisteredPackages()
                    .Where(p => p != null)
                    .OrderBy(p => p.name, StringComparer.Ordinal)
                    .Select(p => new PackageReport
                    {
                        name = p.name,
                        displayName = p.displayName,
                        version = p.version,
                        source = p.source.ToString(),
                        isDirectDependency = p.isDirectDependency
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Package一覧を取得できませんでした: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Transform以下のHierarchyを再帰的に取得
        /// </summary>
        /// <param name="transform">現在処理しているTransform</param>
        /// <param name="root">Hierarchy Pathの基準となるルートTransform</param>
        /// <returns>GameObjectの状態、Transform、Component一覧、子階層を含むHierarchy情報</returns>
        private static HierarchyNode BuildHierarchy(Transform transform, Transform root)
        {
            var node = new HierarchyNode
            {
                name = transform.name,
                path = GetHierarchyPath(transform, root),
                activeSelf = transform.gameObject.activeSelf,
                activeInHierarchy = transform.gameObject.activeInHierarchy,
                tag = transform.gameObject.tag,
                layer = transform.gameObject.layer,
                layerName = LayerMask.LayerToName(transform.gameObject.layer),
                localPosition = SerializableVector3.From(transform.localPosition),
                localRotation = SerializableQuaternion.From(transform.localRotation),
                localScale = SerializableVector3.From(transform.localScale),
                componentTypes = transform.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().FullName)
                    .ToList()
            };

            foreach (Transform child in transform)
            {
                node.children.Add(BuildHierarchy(child, root));
            }

            return node;
        }

        /// <summary>
        /// ルート以下のすべてのSkinnedMeshRenderer情報を取得
        /// </summary>
        /// <param name="rootObject">走査対象のルートGameObject</param>
        /// <returns>Mesh、Bone、Material、BlendShapeなどを含むSkinnedMeshRenderer情報一覧</returns>
        private static List<SkinnedMeshReport> BuildSkinnedMeshReports(GameObject rootObject)
        {
            var root = rootObject.transform;
            var reports = new List<SkinnedMeshReport>();

            foreach (var renderer in rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                var report = new SkinnedMeshReport
                {
                    path = GetHierarchyPath(renderer.transform, root),
                    enabled = renderer.enabled,
                    updateWhenOffscreen = renderer.updateWhenOffscreen,
                    quality = renderer.quality.ToString(),
                    rootBone = GetObjectReference(renderer.rootBone, root),
                    bounds = SerializableBounds.From(renderer.localBounds),
                    mesh = BuildMeshInfo(mesh),
                    bones = renderer.bones
                        .Select(b => GetObjectReference(b, root))
                        .ToList(),
                    materials = renderer.sharedMaterials
                        .Select(m => GetObjectReference(m, root))
                        .ToList()
                };

                if (mesh != null)
                {
                    report.blendShapes = BuildBlendShapeReports(renderer, mesh);
                }

                reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// ルート以下のMeshRendererとMeshFilter情報を取得
        /// </summary>
        /// <param name="rootObject">走査対象のルートGameObject</param>
        /// <returns>MeshとMaterial参照を含むMeshRenderer情報一覧</returns>
        private static List<StaticMeshReport> BuildStaticMeshReports(GameObject rootObject)
        {
            var root = rootObject.transform;
            var reports = new List<StaticMeshReport>();

            foreach (var renderer in rootObject.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                reports.Add(new StaticMeshReport
                {
                    path = GetHierarchyPath(renderer.transform, root),
                    enabled = renderer.enabled,
                    mesh = BuildMeshInfo(filter != null ? filter.sharedMesh : null),
                    materials = renderer.sharedMaterials
                        .Select(m => GetObjectReference(m, root))
                        .ToList()
                });
            }

            return reports;
        }

        /// <summary>
        /// Meshの比較に使用する基本情報を取得
        /// </summary>
        /// <param name="mesh">情報を取得するMesh</param>
        /// <returns>頂点数、三角形数、SubMesh数、BlendShape数などを格納したMesh情報。meshがnullの場合はnull</returns>
        private static MeshInfo BuildMeshInfo(Mesh mesh)
        {
            if (mesh == null)
            {
                return null;
            }

            long indexCount = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                indexCount += (long)mesh.GetIndexCount(i);
            }

            return new MeshInfo
            {
                name = mesh.name,
                assetPath = AssetDatabase.GetAssetPath(mesh),
                vertexCount = mesh.vertexCount,
                indexCount = indexCount,
                triangleCount = indexCount / 3,
                subMeshCount = mesh.subMeshCount,
                bindPoseCount = mesh.bindposes != null ? mesh.bindposes.Length : 0,
                blendShapeCount = mesh.blendShapeCount,
                bounds = SerializableBounds.From(mesh.bounds)
            };
        }

        /// <summary>
        /// SkinnedMeshRendererに存在するBlendShape情報を取得
        /// </summary>
        /// <param name="renderer">現在のBlendShape Weightを取得するSkinnedMeshRenderer</param>
        /// <param name="mesh">BlendShape定義を取得するMesh</param>
        /// <returns>BlendShape名、現在Weight、Frame情報を含むBlendShape一覧</returns>
        private static List<BlendShapeReport> BuildBlendShapeReports(
            SkinnedMeshRenderer renderer,
            Mesh mesh)
        {
            var reports = new List<BlendShapeReport>();

            for (var shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
            {
                var shape = new BlendShapeReport
                {
                    index = shapeIndex,
                    name = mesh.GetBlendShapeName(shapeIndex),
                    currentWeight = renderer.GetBlendShapeWeight(shapeIndex),
                    frameCount = mesh.GetBlendShapeFrameCount(shapeIndex)
                };

                for (var frameIndex = 0; frameIndex < shape.frameCount; frameIndex++)
                {
                    shape.frames.Add(BuildBlendShapeFrameReport(mesh, shapeIndex, frameIndex));
                }

                reports.Add(shape);
            }

            return reports;
        }

        /// <summary>
        /// BlendShape Frameの頂点・法線・接線Deltaを統計化
        /// </summary>
        /// <param name="mesh">BlendShapeを保持するMesh</param>
        /// <param name="shapeIndex">対象BlendShapeのIndex</param>
        /// <param name="frameIndex">対象BlendShape FrameのIndex</param>
        /// <returns>影響頂点数、最大Delta、平均Deltaなどを格納したBlendShape Frame情報</returns>
        private static BlendShapeFrameReport BuildBlendShapeFrameReport(
            Mesh mesh,
            int shapeIndex,
            int frameIndex)
        {
            var count = mesh.vertexCount;
            var deltaVertices = new Vector3[count];
            var deltaNormals = new Vector3[count];
            var deltaTangents = new Vector3[count];

            mesh.GetBlendShapeFrameVertices(
                shapeIndex,
                frameIndex,
                deltaVertices,
                deltaNormals,
                deltaTangents);

            var affectedVertexCount = 0;
            var affectedNormalCount = 0;
            var affectedTangentCount = 0;
            var maxVertexDelta = 0.0f;
            var maxNormalDelta = 0.0f;
            var maxTangentDelta = 0.0f;
            double sumVertexDelta = 0.0;

            for (var i = 0; i < count; i++)
            {
                var vertexMagnitude = deltaVertices[i].magnitude;
                var normalMagnitude = deltaNormals[i].magnitude;
                var tangentMagnitude = deltaTangents[i].magnitude;

                if (vertexMagnitude > DeltaEpsilon)
                {
                    affectedVertexCount++;
                    maxVertexDelta = Mathf.Max(maxVertexDelta, vertexMagnitude);
                    sumVertexDelta += vertexMagnitude;
                }

                if (normalMagnitude > DeltaEpsilon)
                {
                    affectedNormalCount++;
                    maxNormalDelta = Mathf.Max(maxNormalDelta, normalMagnitude);
                }

                if (tangentMagnitude > DeltaEpsilon)
                {
                    affectedTangentCount++;
                    maxTangentDelta = Mathf.Max(maxTangentDelta, tangentMagnitude);
                }
            }

            return new BlendShapeFrameReport
            {
                frameIndex = frameIndex,
                weight = mesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex),
                affectedVertexCount = affectedVertexCount,
                affectedNormalCount = affectedNormalCount,
                affectedTangentCount = affectedTangentCount,
                maxVertexDelta = maxVertexDelta,
                averageAffectedVertexDelta = affectedVertexCount > 0
                    ? (float)(sumVertexDelta / affectedVertexCount)
                    : 0.0f,
                maxNormalDelta = maxNormalDelta,
                maxTangentDelta = maxTangentDelta,
                hasNormalDelta = affectedNormalCount > 0,
                hasTangentDelta = affectedTangentCount > 0
            };
        }

        /// <summary>
        /// ルート以下のRendererが参照するMaterialを重複排除して取得
        /// </summary>
        /// <param name="report">収集済みのアバターレポート</param>
        /// <param name="rootObject">Material参照を探索するルートGameObject</param>
        /// <returns>Shader名とShader Propertyを含むMaterial情報一覧</returns>
        private static List<MaterialReport> BuildMaterialReports(
            AvatarReport report,
            GameObject rootObject)
        {
            var materials = new Dictionary<int, Material>();

            foreach (var renderer in rootObject.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null)
                    {
                        materials[material.GetInstanceID()] = material;
                    }
                }
            }

            return materials.Values
                .OrderBy(m => AssetDatabase.GetAssetPath(m), StringComparer.Ordinal)
                .ThenBy(m => m.name, StringComparer.Ordinal)
                .Select(BuildMaterialReport)
                .ToList();
        }

        /// <summary>
        /// MaterialとShaderが公開しているProperty情報を取得
        /// </summary>
        /// <param name="material">情報を取得するMaterial</param>
        /// <returns>Shader、Render Queue、Keyword、各Property値を格納したMaterial情報</returns>
        private static MaterialReport BuildMaterialReport(Material material)
        {
            var report = new MaterialReport
            {
                name = material.name,
                assetPath = AssetDatabase.GetAssetPath(material),
                shader = material.shader != null ? material.shader.name : null,
                shaderAssetPath = material.shader != null
                    ? AssetDatabase.GetAssetPath(material.shader)
                    : null,
                renderQueue = material.renderQueue,
                enableInstancing = material.enableInstancing,
                doubleSidedGI = material.doubleSidedGI,
                globalIlluminationFlags = material.globalIlluminationFlags.ToString(),
                shaderKeywords = material.shaderKeywords
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList()
            };

            if (material.shader == null)
            {
                return report;
            }

            var shader = material.shader;
            var propertyCount = ShaderUtil.GetPropertyCount(shader);

            for (var i = 0; i < propertyCount; i++)
            {
                var propertyName = ShaderUtil.GetPropertyName(shader, i);
                if (!material.HasProperty(propertyName))
                {
                    continue;
                }

                var type = ShaderUtil.GetPropertyType(shader, i).ToString();
                var property = new MaterialPropertyReport
                {
                    index = i,
                    name = propertyName,
                    description = ShaderUtil.GetPropertyDescription(shader, i),
                    type = type
                };

                switch (type)
                {
                    case "Color":
                        property.colorValue = SerializableColor.From(
                            material.GetColor(propertyName));
                        break;

                    case "Vector":
                        property.vectorValue = SerializableVector4.From(
                            material.GetVector(propertyName));
                        break;

                    case "Float":
                    case "Range":
                        property.floatValue = material.GetFloat(propertyName);
                        break;

                    case "Int":
                        // Unity 2022系ではInt PropertyもGetFloatで取得可能なため、
                        // バージョン差を避けてfloatValueへ格納し
                        property.floatValue = material.GetFloat(propertyName);
                        break;

                    case "TexEnv":
                    case "Texture":
                        var texture = material.GetTexture(propertyName);
                        property.texture = GetObjectReference(texture, null);
                        property.textureScale = SerializableVector2.From(
                            material.GetTextureScale(propertyName));
                        property.textureOffset = SerializableVector2.From(
                            material.GetTextureOffset(propertyName));
                        break;

                    default:
                        // 将来Shader Property種別が増えた場合でも出力自体は継続し
                        break;
                }

                report.properties.Add(property);
            }

            return report;
        }

        /// <summary>
        /// Materialから参照されているTextureを重複排除して取得
        /// </summary>
        /// <param name="materials">Texture参照元となるMaterial情報一覧</param>
        /// <returns>Texture本体とTextureImporter設定を含むTexture情報一覧</returns>
        private static List<TextureReport> BuildTextureReports(List<MaterialReport> materials)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var textures = new List<TextureReport>();

            foreach (var material in materials)
            {
                foreach (var property in material.properties)
                {
                    if (property.texture == null || string.IsNullOrEmpty(property.texture.assetPath))
                    {
                        continue;
                    }

                    if (!paths.Add(property.texture.assetPath))
                    {
                        continue;
                    }

                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(property.texture.assetPath);
                    if (texture != null)
                    {
                        textures.Add(BuildTextureReport(texture));
                    }
                }
            }

            return textures
                .OrderBy(t => t.assetPath, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Texture本体とTextureImporterの主要設定を取得
        /// </summary>
        /// <param name="texture">情報を取得するTexture</param>
        /// <returns>解像度、Format、Mipmap、圧縮設定などを格納したTexture情報</returns>
        private static TextureReport BuildTextureReport(Texture texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            var report = new TextureReport
            {
                name = texture.name,
                assetPath = path,
                type = texture.GetType().FullName,
                width = texture.width,
                height = texture.height,
                dimension = texture.dimension.ToString(),
                graphicsFormat = texture.graphicsFormat.ToString(),
                mipmapCount = texture.mipmapCount,
                wrapMode = texture.wrapMode.ToString(),
                filterMode = texture.filterMode.ToString(),
                anisoLevel = texture.anisoLevel
            };

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                report.importer = new TextureImporterReport
                {
                    textureType = importer.textureType.ToString(),
                    textureShape = importer.textureShape.ToString(),
                    sRGBTexture = importer.sRGBTexture,
                    alphaSource = importer.alphaSource.ToString(),
                    alphaIsTransparency = importer.alphaIsTransparency,
                    mipmapEnabled = importer.mipmapEnabled,
                    streamingMipmaps = importer.streamingMipmaps,
                    streamingMipmapsPriority = importer.streamingMipmapsPriority,
                    borderMipmap = importer.borderMipmap,
                    mipMapsPreserveCoverage = importer.mipMapsPreserveCoverage,
                    fadeout = importer.fadeout,
                    wrapMode = importer.wrapMode.ToString(),
                    filterMode = importer.filterMode.ToString(),
                    anisoLevel = importer.anisoLevel,
                    maxTextureSize = importer.maxTextureSize,
                    textureCompression = importer.textureCompression.ToString(),
                    compressionQuality = importer.compressionQuality,
                    crunchedCompression = importer.crunchedCompression,
                    npotScale = importer.npotScale.ToString(),
                    isReadable = importer.isReadable
                };
            }

            return report;
        }

        /// <summary>
        /// ルート以下のAnimatorとAnimatorController情報を取得
        /// </summary>
        /// <param name="rootObject">Animatorを探索するルートGameObject</param>
        /// <returns>Animator設定とAnimatorController構造を含むAnimator情報一覧</returns>
        private static List<AnimatorReport> BuildAnimatorReports(GameObject rootObject)
        {
            var root = rootObject.transform;
            var reports = new List<AnimatorReport>();

            foreach (var animator in rootObject.GetComponentsInChildren<Animator>(true))
            {
                var report = new AnimatorReport
                {
                    path = GetHierarchyPath(animator.transform, root),
                    enabled = animator.enabled,
                    applyRootMotion = animator.applyRootMotion,
                    updateMode = animator.updateMode.ToString(),
                    cullingMode = animator.cullingMode.ToString(),
                    isHuman = animator.isHuman,
                    avatar = GetObjectReference(animator.avatar, root),
                    runtimeAnimatorController = GetObjectReference(
                        animator.runtimeAnimatorController,
                        root)
                };

                var controller = ResolveAnimatorController(animator.runtimeAnimatorController);
                if (controller != null)
                {
                    report.controller = BuildAnimatorControllerReport(controller);
                }

                reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// RuntimeAnimatorControllerから元のAnimatorControllerを取得
        /// </summary>
        /// <param name="runtimeController">Animatorに設定されているRuntimeAnimatorController</param>
        /// <returns>取得できたAnimatorController。取得できない場合はnull</returns>
        private static AnimatorController ResolveAnimatorController(
            RuntimeAnimatorController runtimeController)
        {
            if (runtimeController is AnimatorController direct)
            {
                return direct;
            }

            if (runtimeController is AnimatorOverrideController overrideController)
            {
                return overrideController.runtimeAnimatorController as AnimatorController;
            }

            return null;
        }

        /// <summary>
        /// AnimatorControllerのParameter、Layer、State情報を取得
        /// </summary>
        /// <param name="controller">解析対象のAnimatorController</param>
        /// <returns>AnimatorControllerの比較用情報</returns>
        private static AnimatorControllerReport BuildAnimatorControllerReport(
            AnimatorController controller)
        {
            var report = new AnimatorControllerReport
            {
                name = controller.name,
                assetPath = AssetDatabase.GetAssetPath(controller)
            };

            foreach (var parameter in controller.parameters)
            {
                report.parameters.Add(new AnimatorParameterReport
                {
                    name = parameter.name,
                    type = parameter.type.ToString(),
                    defaultFloat = parameter.defaultFloat,
                    defaultInt = parameter.defaultInt,
                    defaultBool = parameter.defaultBool
                });
            }

            for (var layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                var layer = controller.layers[layerIndex];
                var layerReport = new AnimatorLayerReport
                {
                    index = layerIndex,
                    name = layer.name,
                    defaultWeight = layer.defaultWeight,
                    blendingMode = layer.blendingMode.ToString(),
                    iKPass = layer.iKPass,
                    syncedLayerIndex = layer.syncedLayerIndex,
                    avatarMask = GetObjectReference(layer.avatarMask, null)
                };

                CollectAnimatorStates(
                    layer.stateMachine,
                    layer.name,
                    layerReport.states);

                report.layers.Add(layerReport);
            }

            return report;
        }

        /// <summary>
        /// AnimatorStateMachineを再帰的に走査してState情報を収集
        /// </summary>
        /// <param name="stateMachine">現在処理しているAnimatorStateMachine</param>
        /// <param name="stateMachinePath">State Pathの基準となるStateMachine Path</param>
        /// <param name="states">取得したState情報の追加先</param>
        private static void CollectAnimatorStates(
            AnimatorStateMachine stateMachine,
            string stateMachinePath,
            List<AnimatorStateReport> states)
        {
            foreach (var childState in stateMachine.states)
            {
                var state = childState.state;
                states.Add(new AnimatorStateReport
                {
                    path = stateMachinePath + "/" + state.name,
                    name = state.name,
                    speed = state.speed,
                    mirror = state.mirror,
                    writeDefaultValues = state.writeDefaultValues,
                    motion = GetObjectReference(state.motion, null)
                });
            }

            foreach (var childMachine in stateMachine.stateMachines)
            {
                CollectAnimatorStates(
                    childMachine.stateMachine,
                    stateMachinePath + "/" + childMachine.stateMachine.name,
                    states);
            }
        }

        /// <summary>
        /// VRChat関連Componentを抽出し、Avatar Descriptorを専用形式で解析
        /// </summary>
        /// <param name="rootObject">VRChat関連Componentを探索するルートGameObject</param>
        /// <returns>Avatar Descriptor専用情報とVRChat関連Component一覧</returns>
        private static VRChatReport BuildVRChatReport(GameObject rootObject)
        {
            var root = rootObject.transform;
            var report = new VRChatReport();

            foreach (var component in rootObject.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != "VRCAvatarDescriptor")
                {
                    continue;
                }

                report.avatarDescriptors.Add(BuildVRCAvatarDescriptorReport(component, root));
            }

            return report;
        }

        /// <summary>
        /// VRC Avatar Descriptorの主要設定を比較用形式へ変換
        /// </summary>
        /// <param name="descriptor">解析対象のVRC Avatar Descriptor Component</param>
        /// <param name="root">Hierarchy Pathの基準となるルートTransform</param>
        /// <returns>View Position、Lip Sync、Eye Look、Playable Layers、Expressionsなどを含むAvatar Descriptor情報</returns>
        private static VRCAvatarDescriptorReport BuildVRCAvatarDescriptorReport(
            Component descriptor,
            Transform root)
        {
            var so = new SerializedObject(descriptor);
            var report = new VRCAvatarDescriptorReport
            {
                gameObjectPath = GetHierarchyPath(descriptor.transform, root),
                type = descriptor.GetType().FullName,
                viewPosition = GetVector3Property(so, "ViewPosition"),
                lipSync = BuildLipSyncReport(so, root),
                eyeLook = BuildEyeLookReport(so, root),
                playableLayers = BuildPlayableLayersReport(so, root),
                expressions = BuildExpressionsReport(so, root),
                rawProperties = BuildSerializedPropertyTree(so, root)
            };

            var colliders = FindFirstProperty(so, "collider_config", "colliderConfig", "colliders");
            if (colliders != null)
            {
                report.colliders = BuildSerializedPropertyTree(colliders, root);
            }

            return report;
        }

        /// <summary>
        /// VRC Avatar DescriptorのLip Sync設定を取得
        /// </summary>
        /// <param name="so">VRC Avatar DescriptorのSerializedObject</param>
        /// <param name="root">Hierarchy Pathの基準となるルートTransform</param>
        /// <returns>Lip Sync方式、Viseme Renderer、Viseme割り当てを含むLip Sync情報</returns>
        private static VRCLipSyncReport BuildLipSyncReport(SerializedObject so, Transform root)
        {
            var report = new VRCLipSyncReport();
            var lipSync = so.FindProperty("lipSync");
            if (lipSync != null)
            {
                report.mode = GetSerializedValue(lipSync, root);
            }

            var mesh = so.FindProperty("VisemeSkinnedMesh");
            if (mesh != null)
            {
                report.visemeSkinnedMesh = GetObjectReference(mesh.objectReferenceValue, root);
            }

            var jawBone = so.FindProperty("lipSyncJawBone");
            if (jawBone != null)
            {
                report.jawBone = GetObjectReference(jawBone.objectReferenceValue, root);
            }

            var jawClosed = so.FindProperty("lipSyncJawClosed");
            if (jawClosed != null)
            {
                report.jawClosed = GetSerializedValue(jawClosed, root);
            }

            var jawOpen = so.FindProperty("lipSyncJawOpen");
            if (jawOpen != null)
            {
                report.jawOpen = GetSerializedValue(jawOpen, root);
            }

            var mouthOpen = so.FindProperty("MouthOpenBlendShapeName");
            if (mouthOpen != null)
            {
                report.mouthOpenBlendShapeName = mouthOpen.stringValue;
            }

            var visemeNames = new[]
            {
                "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS",
                "nn", "RR", "aa", "E", "ih", "oh", "ou"
            };

            var visemes = so.FindProperty("VisemeBlendShapes");
            if (visemes != null && visemes.isArray)
            {
                for (var i = 0; i < visemes.arraySize; i++)
                {
                    var element = visemes.GetArrayElementAtIndex(i);
                    report.visemes.Add(new VRCVisemeReport
                    {
                        index = i,
                        viseme = i < visemeNames.Length ? visemeNames[i] : $"index_{i}",
                        blendShape = element.propertyType == SerializedPropertyType.String
                            ? element.stringValue
                            : GetSerializedValue(element, root)
                    });
                }
            }

            return report;
        }

        /// <summary>
        /// VRC Avatar DescriptorのEye Look設定を取得
        /// </summary>
        /// <param name="so">VRC Avatar DescriptorのSerializedObject</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Eye Look有効状態と詳細設定</returns>
        private static VRCEyeLookReport BuildEyeLookReport(SerializedObject so, Transform root)
        {
            var report = new VRCEyeLookReport();
            var enabled = so.FindProperty("enableEyeLook");
            if (enabled != null)
            {
                report.enabled = enabled.boolValue;
            }

            var settings = so.FindProperty("customEyeLookSettings");
            if (settings != null)
            {
                report.settings = BuildSerializedPropertyTree(settings, root);
            }

            return report;
        }

        /// <summary>
        /// VRC Avatar DescriptorのPlayable Layers設定を取得
        /// </summary>
        /// <param name="so">VRC Avatar DescriptorのSerializedObject</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Base、Additive、Gesture、Action、FXなどのPlayable Layer情報</returns>
        private static VRCPlayableLayersReport BuildPlayableLayersReport(
            SerializedObject so,
            Transform root)
        {
            var report = new VRCPlayableLayersReport();
            var customize = so.FindProperty("customizeAnimationLayers");
            if (customize != null)
            {
                report.customized = customize.boolValue;
            }

            report.baseLayers = BuildPlayableLayerArray(so.FindProperty("baseAnimationLayers"), root);
            report.specialLayers = BuildPlayableLayerArray(so.FindProperty("specialAnimationLayers"), root);
            return report;
        }

        /// <summary>
        /// Playable Layer配列から各Layerの設定を取得
        /// </summary>
        /// <param name="array">Playable Layer構造体を保持するSerializedProperty配列</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Layer種別、Default状態、Animator Controllerなどを含むPlayable Layer一覧</returns>
        private static List<VRCPlayableLayerReport> BuildPlayableLayerArray(
            SerializedProperty array,
            Transform root)
        {
            var reports = new List<VRCPlayableLayerReport>();
            if (array == null || !array.isArray)
            {
                return reports;
            }

            for (var i = 0; i < array.arraySize; i++)
            {
                var element = array.GetArrayElementAtIndex(i);
                var type = element.FindPropertyRelative("type");
                var isDefault = element.FindPropertyRelative("isDefault");
                var controller = element.FindPropertyRelative("animatorController");

                var layer = new VRCPlayableLayerReport
                {
                    index = i,
                    type = type != null ? GetSerializedValue(type, root) : null,
                    isDefault = isDefault != null && isDefault.boolValue,
                    animatorController = controller != null
                        ? GetObjectReference(controller.objectReferenceValue, root)
                        : null,
                    rawProperties = BuildSerializedPropertyTree(element, root)
                };

                if (controller != null)
                {
                    var runtimeController = controller.objectReferenceValue as RuntimeAnimatorController;
                    var animatorController = ResolveAnimatorController(runtimeController);
                    if (animatorController != null)
                    {
                        layer.controller = BuildAnimatorControllerReport(animatorController);
                    }
                }

                reports.Add(layer);
            }

            return reports;
        }

        /// <summary>
        /// VRC Avatar DescriptorのExpressions設定を取得
        /// </summary>
        /// <param name="so">VRC Avatar DescriptorのSerializedObject</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Expressions Menu、Expression Parameters、および各Asset内部の設定情報</returns>
        private static VRCExpressionsReport BuildExpressionsReport(
            SerializedObject so,
            Transform root)
        {
            var report = new VRCExpressionsReport();
            var custom = so.FindProperty("customExpressions");
            if (custom != null)
            {
                report.customized = custom.boolValue;
            }

            var menu = so.FindProperty("expressionsMenu");
            if (menu != null)
            {
                report.menu = GetObjectReference(menu.objectReferenceValue, root);
                if (menu.objectReferenceValue != null)
                {
                    report.menuProperties = BuildSerializedPropertyTree(
                        new SerializedObject(menu.objectReferenceValue), root);
                }
            }

            var parameters = so.FindProperty("expressionParameters");
            if (parameters != null)
            {
                report.parameters = GetObjectReference(parameters.objectReferenceValue, root);
                if (parameters.objectReferenceValue != null)
                {
                    report.parameterProperties = BuildSerializedPropertyTree(
                        new SerializedObject(parameters.objectReferenceValue), root);
                }
            }

            return report;
        }

        /// <summary>
        /// SerializedProperty以下のプロパティを階層構造を保ったまま再帰的に取得
        /// </summary>
        /// <param name="serializedObject">走査対象のSerializedObject</param>
        /// <param name="root">Hierarchy Pathの基準となるTransform</param>
        /// <returns>取得したSerializedPropertyの階層情報</returns>
        private static List<SerializedPropertyNode> BuildSerializedPropertyTree(
            SerializedObject serializedObject,
            Transform root)
        {
            var result = new List<SerializedPropertyNode>();
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script")
                {
                    continue;
                }

                result.AddRange(BuildSerializedPropertyTree(iterator.Copy(), root));
            }

            return result;
        }

        /// <summary>
        /// SerializedProperty以下のプロパティを階層構造を保ったまま再帰的に取得
        /// </summary>
        /// <param name="property">取得対象のSerializedProperty</param>
        /// <param name="root">Hierarchy Pathの基準となるTransform</param>
        /// <returns>取得したSerializedPropertyの階層情報</returns>
        private static List<SerializedPropertyNode> BuildSerializedPropertyTree(
            SerializedProperty property,
            Transform root)
        {
            var nodes = new List<SerializedPropertyNode>();
            var copy = property.Copy();
            var end = copy.GetEndProperty();
            var startDepth = copy.depth;
            nodes.Add(BuildSerializedPropertyNode(copy, root));

            if (!copy.hasVisibleChildren)
            {
                return nodes;
            }

            var iterator = copy.Copy();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                if (iterator.depth == startDepth + 1)
                {
                    nodes[0].children.Add(BuildSerializedPropertyNodeWithChildren(iterator, root));
                }
            }

            return nodes;
        }

        /// <summary>
        /// SerializedPropertyを子要素を含む階層Nodeへ変換
        /// </summary>
        /// <param name="property">変換対象のSerializedProperty</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>子要素を含むSerializedPropertyの階層Node</returns>
        private static SerializedPropertyNode BuildSerializedPropertyNodeWithChildren(
            SerializedProperty property,
            Transform root)
        {
            var node = BuildSerializedPropertyNode(property, root);
            if (!property.hasVisibleChildren)
            {
                return node;
            }

            var copy = property.Copy();
            var end = copy.GetEndProperty();
            var depth = copy.depth;
            var iterator = copy.Copy();
            var enterChildren = true;

            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                if (iterator.depth == depth + 1)
                {
                    node.children.Add(BuildSerializedPropertyNodeWithChildren(iterator, root));
                }
            }

            return node;
        }

        /// <summary>
        /// SerializedProperty単体を比較用Nodeへ変換
        /// </summary>
        /// <param name="property">変換対象のSerializedProperty</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Property名、型、値、ObjectReferenceなどを格納したNode</returns>
        private static SerializedPropertyNode BuildSerializedPropertyNode(
            SerializedProperty property,
            Transform root)
        {
            var node = new SerializedPropertyNode
            {
                path = property.propertyPath,
                name = property.name,
                displayName = property.displayName,
                type = property.propertyType.ToString(),
                isArray = property.isArray,
                arraySize = property.isArray ? property.arraySize : 0,
                value = GetSerializedValue(property, root)
            };

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                node.objectReference = GetObjectReference(property.objectReferenceValue, root);
            }

            return node;
        }

        /// <summary>
        /// SerializedPropertyの値を比較用の文字列へ変換
        /// </summary>
        /// <param name="property">値を取得するSerializedProperty</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Property種別に応じて文字列化した値</returns>
        private static string GetSerializedValue(SerializedProperty property, Transform root)
        {
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        return property.longValue.ToString();
                    case SerializedPropertyType.Boolean:
                        return property.boolValue ? "true" : "false";
                    case SerializedPropertyType.Float:
                        return property.doubleValue.ToString("R");
                    case SerializedPropertyType.String:
                        return property.stringValue;
                    case SerializedPropertyType.Color:
                        return FormatColor(property.colorValue);
                    case SerializedPropertyType.ObjectReference:
                        var reference = GetObjectReference(property.objectReferenceValue, root);
                        return reference != null
                            ? (!string.IsNullOrEmpty(reference.hierarchyPath)
                                ? reference.hierarchyPath
                                : reference.assetPath)
                            : null;
                    case SerializedPropertyType.LayerMask:
                        return property.intValue.ToString();
                    case SerializedPropertyType.Enum:
                        return property.enumDisplayNames != null &&
                               property.enumValueIndex >= 0 &&
                               property.enumValueIndex < property.enumDisplayNames.Length
                            ? property.enumDisplayNames[property.enumValueIndex]
                            : property.enumValueIndex.ToString();
                    case SerializedPropertyType.Vector2:
                        return FormatVector2(property.vector2Value);
                    case SerializedPropertyType.Vector3:
                        return FormatVector3(property.vector3Value);
                    case SerializedPropertyType.Vector4:
                        return FormatVector4(property.vector4Value);
                    case SerializedPropertyType.Quaternion:
                        return FormatQuaternion(property.quaternionValue);
                    case SerializedPropertyType.ArraySize:
                        return property.intValue.ToString();
                    case SerializedPropertyType.AnimationCurve:
                        return property.animationCurveValue != null
                            ? $"keys={property.animationCurveValue.length}"
                            : "null";
                    case SerializedPropertyType.Bounds:
                        return property.boundsValue.ToString();
                    case SerializedPropertyType.Rect:
                        return property.rectValue.ToString();
                    case SerializedPropertyType.Vector2Int:
                        return property.vector2IntValue.ToString();
                    case SerializedPropertyType.Vector3Int:
                        return property.vector3IntValue.ToString();
                    case SerializedPropertyType.RectInt:
                        return property.rectIntValue.ToString();
                    case SerializedPropertyType.BoundsInt:
                        return property.boundsIntValue.ToString();
                    default:
                        return property.hasVisibleChildren ? "<complex>" : property.type;
                }
            }
            catch (Exception ex)
            {
                return $"<read error: {ex.Message}>";
            }
        }

        /// <summary>
        /// 候補名から最初に存在するSerializedPropertyを取得
        /// </summary>
        /// <param name="so">検索対象のSerializedObject</param>
        /// <param name="names">検索する候補Property名</param>
        /// <returns>最初に見つかったSerializedProperty。見つからない場合はnull</returns>
        private static SerializedProperty FindFirstProperty(SerializedObject so, params string[] names)
        {
            foreach (var name in names)
            {
                var property = so.FindProperty(name);
                if (property != null)
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>
        /// 指定名のVector3 Propertyを取得
        /// </summary>
        /// <param name="so">検索対象のSerializedObject</param>
        /// <param name="name">取得するProperty名</param>
        /// <returns>取得したVector3値。見つからない場合はnull</returns>
        private static SerializableVector3 GetVector3Property(SerializedObject so, string name)
        {
            var property = so.FindProperty(name);
            return property != null && property.propertyType == SerializedPropertyType.Vector3
                ? SerializableVector3.From(property.vector3Value)
                : null;
        }

        /// <summary>
        /// ルート以下のComponentを汎用SerializedProperty形式で取得
        /// </summary>
        /// <param name="rootObject">Componentを探索するルートGameObject</param>
        /// <returns>Component型、Assembly、SerializedPropertyを含むComponent情報一覧</returns>
        private static List<ComponentReport> BuildComponentReports(GameObject rootObject)
        {
            var root = rootObject.transform;
            var reports = new List<ComponentReport>();

            foreach (var component in rootObject.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();

                // 専用セクションで詳細を出しているUnity標準Componentは二重出力を避け
                if (component is Transform ||
                    component is SkinnedMeshRenderer ||
                    component is MeshRenderer ||
                    component is MeshFilter ||
                    component is Animator)
                {
                    continue;
                }

                var report = new ComponentReport
                {
                    gameObjectPath = GetHierarchyPath(component.transform, root),
                    type = type.FullName,
                    assembly = type.Assembly.GetName().Name
                };

                try
                {
                    var serializedObject = new SerializedObject(component);
                    var iterator = serializedObject.GetIterator();
                    var enterChildren = true;

                    while (iterator.NextVisible(enterChildren))
                    {
                        enterChildren = true;

                        if (iterator.propertyPath == "m_Script")
                        {
                            continue;
                        }

                        report.properties.Add(BuildSerializedPropertyReport(iterator, root));
                    }
                }
                catch (Exception ex)
                {
                    report.readError = ex.Message;
                }

                reports.Add(report);
            }

            return reports
                .OrderBy(r => r.gameObjectPath, StringComparer.Ordinal)
                .ThenBy(r => r.type, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 1つのSerializedPropertyを比較用形式へ変換
        /// </summary>
        /// <param name="property">変換対象のSerializedProperty</param>
        /// <param name="root">ObjectReferenceをHierarchy Pathへ変換する基準Transform</param>
        /// <returns>Property Path、表示名、型、値、ObjectReferenceを格納したSerializedProperty情報</returns>
        private static SerializedPropertyReport BuildSerializedPropertyReport(
            SerializedProperty property,
            Transform root)
        {
            var report = new SerializedPropertyReport
            {
                path = property.propertyPath,
                displayName = property.displayName,
                type = property.propertyType.ToString()
            };

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        report.value = property.longValue.ToString();
                        break;

                    case SerializedPropertyType.Boolean:
                        report.value = property.boolValue ? "true" : "false";
                        break;

                    case SerializedPropertyType.Float:
                        report.value = property.doubleValue.ToString("R");
                        break;

                    case SerializedPropertyType.String:
                        report.value = property.stringValue;
                        break;

                    case SerializedPropertyType.Color:
                        report.value = FormatColor(property.colorValue);
                        break;

                    case SerializedPropertyType.ObjectReference:
                        report.objectReference = GetObjectReference(
                            property.objectReferenceValue,
                            root);
                        break;

                    case SerializedPropertyType.LayerMask:
                        report.value = property.intValue.ToString();
                        break;

                    case SerializedPropertyType.Enum:
                        report.value = property.enumDisplayNames != null &&
                                       property.enumValueIndex >= 0 &&
                                       property.enumValueIndex < property.enumDisplayNames.Length
                            ? property.enumDisplayNames[property.enumValueIndex]
                            : property.enumValueIndex.ToString();
                        break;

                    case SerializedPropertyType.Vector2:
                        report.value = FormatVector2(property.vector2Value);
                        break;

                    case SerializedPropertyType.Vector3:
                        report.value = FormatVector3(property.vector3Value);
                        break;

                    case SerializedPropertyType.Vector4:
                        report.value = FormatVector4(property.vector4Value);
                        break;

                    case SerializedPropertyType.Rect:
                        report.value = property.rectValue.ToString();
                        break;

                    case SerializedPropertyType.ArraySize:
                        report.value = property.intValue.ToString();
                        break;

                    case SerializedPropertyType.Character:
                        report.value = ((char)property.intValue).ToString();
                        break;

                    case SerializedPropertyType.AnimationCurve:
                        report.value = property.animationCurveValue != null
                            ? $"keys={property.animationCurveValue.length}"
                            : "null";
                        break;

                    case SerializedPropertyType.Bounds:
                        report.value = property.boundsValue.ToString();
                        break;

                    case SerializedPropertyType.Quaternion:
                        report.value = FormatQuaternion(property.quaternionValue);
                        break;

                    case SerializedPropertyType.Vector2Int:
                        report.value = property.vector2IntValue.ToString();
                        break;

                    case SerializedPropertyType.Vector3Int:
                        report.value = property.vector3IntValue.ToString();
                        break;

                    case SerializedPropertyType.RectInt:
                        report.value = property.rectIntValue.ToString();
                        break;

                    case SerializedPropertyType.BoundsInt:
                        report.value = property.boundsIntValue.ToString();
                        break;

                    default:
                        report.value = property.hasChildren ? "<complex>" : property.type;
                        break;
                }
            }
            catch (Exception ex)
            {
                report.value = $"<read error: {ex.Message}>";
            }

            return report;
        }

        /// <summary>
        /// アバター全体の主要な集計値を作成
        /// </summary>
        /// <param name="rootObject">集計対象のルートGameObject</param>
        /// <param name="allTransforms">ルート以下の全Transform</param>
        /// <param name="allComponents">ルート以下の全Component</param>
        /// <param name="skinnedMeshes">収集済みSkinnedMeshRenderer情報</param>
        /// <param name="staticMeshes">収集済みMeshRenderer情報</param>
        /// <param name="materials">収集済みMaterial情報</param>
        /// <param name="textures">収集済みTexture情報</param>
        /// <returns>GameObject数、Mesh数、Bone数、Material数、VRChat Component数などの集計情報</returns>
        private static SummaryReport BuildSummary(
            GameObject rootObject,
            Transform[] allTransforms,
            Component[] allComponents,
            List<SkinnedMeshReport> skinnedMeshes,
            List<StaticMeshReport> staticMeshes,
            List<MaterialReport> materials,
            List<TextureReport> textures)
        {
            var uniqueMeshes = new Dictionary<string, MeshInfo>(StringComparer.Ordinal);

            foreach (var report in skinnedMeshes)
            {
                AddUniqueMesh(uniqueMeshes, report.mesh);
            }

            foreach (var report in staticMeshes)
            {
                AddUniqueMesh(uniqueMeshes, report.mesh);
            }

            var uniqueBones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in skinnedMeshes)
            {
                foreach (var bone in renderer.bones)
                {
                    if (bone != null && !string.IsNullOrEmpty(bone.hierarchyPath))
                    {
                        uniqueBones.Add(bone.hierarchyPath);
                    }
                }
            }

            var componentTypes = allComponents
                .Where(c => c != null)
                .GroupBy(c => c.GetType().FullName)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ComponentCountReport
                {
                    type = g.Key,
                    count = g.Count()
                })
                .ToList();

            var allMaterialSlots = rootObject
                .GetComponentsInChildren<Renderer>(true)
                .Sum(r => r.sharedMaterials != null ? r.sharedMaterials.Length : 0);

            return new SummaryReport
            {
                gameObjectCount = allTransforms.Length,
                componentCount = allComponents.Count(c => c != null),
                skinnedMeshRendererCount = skinnedMeshes.Count,
                staticMeshRendererCount = staticMeshes.Count,
                uniqueMeshCount = uniqueMeshes.Count,
                totalUniqueMeshVertices = uniqueMeshes.Values.Sum(m => (long)m.vertexCount),
                totalUniqueMeshTriangles = uniqueMeshes.Values.Sum(m => m.triangleCount),
                uniqueBoneCount = uniqueBones.Count,
                blendShapeCount = skinnedMeshes.Sum(r =>
                    r.blendShapes != null ? r.blendShapes.Count : 0),
                materialSlotCount = allMaterialSlots,
                uniqueMaterialCount = materials.Count,
                uniqueTextureCount = textures.Count,
                physBoneComponentCount = CountTypeName(allComponents, "VRCPhysBone"),
                physBoneColliderComponentCount = CountTypeName(
                    allComponents,
                    "VRCPhysBoneCollider"),
                contactSenderComponentCount = CountTypeName(
                    allComponents,
                    "VRCContactSender"),
                contactReceiverComponentCount = CountTypeName(
                    allComponents,
                    "VRCContactReceiver"),
                componentTypes = componentTypes
            };
        }

        /// <summary>
        /// Mesh情報を重複排除しながら一覧へ追加
        /// </summary>
        /// <param name="meshes">重複排除に使用するMesh情報Dictionary</param>
        /// <param name="mesh">追加対象のMesh情報</param>
        private static void AddUniqueMesh(
            Dictionary<string, MeshInfo> meshes,
            MeshInfo mesh)
        {
            if (mesh == null)
            {
                return;
            }

            var key = $"{mesh.assetPath}::{mesh.name}";
            meshes[key] = mesh;
        }

        /// <summary>
        /// 指定したComponent型名に一致する件数を取得
        /// </summary>
        /// <param name="components">検索対象のComponent配列</param>
        /// <param name="typeName">一致させるComponent型名</param>
        /// <returns>指定した型名に一致するComponent数</returns>
        private static int CountTypeName(Component[] components, string typeName)
        {
            return components.Count(c =>
                c != null &&
                string.Equals(c.GetType().Name, typeName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Unity Objectを比較用の参照情報へ変換
        /// </summary>
        /// <param name="obj">参照情報へ変換するUnity Object</param>
        /// <param name="root">Hierarchy Pathの基準となるTransform</param>
        /// <returns>名前、型、Asset Path、Hierarchy Pathを格納したObject参照情報。objがnullの場合はnull</returns>
        private static ObjectReference GetObjectReference(
            UnityEngine.Object obj,
            Transform root)
        {
            if (obj == null)
            {
                return null;
            }

            var reference = new ObjectReference
            {
                name = obj.name,
                type = obj.GetType().FullName,
                assetPath = AssetDatabase.GetAssetPath(obj)
            };

            if (obj is Component component)
            {
                reference.hierarchyPath = root != null &&
                                          component.transform.IsChildOf(root)
                    ? GetHierarchyPath(component.transform, root)
                    : GetFullScenePath(component.transform);
            }
            else if (obj is GameObject gameObject)
            {
                reference.hierarchyPath = root != null &&
                                          gameObject.transform.IsChildOf(root)
                    ? GetHierarchyPath(gameObject.transform, root)
                    : GetFullScenePath(gameObject.transform);
            }

            return reference;
        }

        /// <summary>
        /// 指定Transformのルートからの相対Hierarchy Pathを取得
        /// </summary>
        /// <param name="transform">Pathを取得するTransform</param>
        /// <param name="root">Pathの基準となるルートTransform</param>
        /// <returns>ルートから対象TransformまでのHierarchy Path</returns>
        private static string GetHierarchyPath(Transform transform, Transform root)
        {
            if (transform == null)
            {
                return null;
            }

            if (root == null)
            {
                return GetFullScenePath(transform);
            }

            var names = new Stack<string>();
            var current = transform;

            while (current != null)
            {
                names.Push(current.name);

                if (current == root)
                {
                    return string.Join("/", names);
                }

                current = current.parent;
            }

            return GetFullScenePath(transform);
        }

        /// <summary>
        /// Scene上の最上位GameObjectから指定TransformまでのHierarchy Pathを取得
        /// </summary>
        /// <param name="transform">Pathを取得するTransform</param>
        /// <returns>Scene上の完全Hierarchy Path</returns>
        private static string GetFullScenePath(Transform transform)
        {
            var names = new Stack<string>();
            var current = transform;

            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        /// <summary>
        /// ファイル名に使用できない文字を置換
        /// </summary>
        /// <param name="value">変換元の文字列</param>
        /// <returns>ファイル名として使用可能な文字列</returns>
        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static string FormatVector2(Vector2 value)
            => $"({value.x:R}, {value.y:R})";

        private static string FormatVector3(Vector3 value)
            => $"({value.x:R}, {value.y:R}, {value.z:R})";

        private static string FormatVector4(Vector4 value)
            => $"({value.x:R}, {value.y:R}, {value.z:R}, {value.w:R})";

        private static string FormatQuaternion(Quaternion value)
            => $"({value.x:R}, {value.y:R}, {value.z:R}, {value.w:R})";

        private static string FormatColor(Color value)
            => $"({value.r:R}, {value.g:R}, {value.b:R}, {value.a:R})";
    }

    /// <summary>
    /// アバター全体のJSONレポート
    /// </summary>
    [Serializable]
    public class AvatarReport
    {
        /// <summary>
        /// JSONレポート形式のバージョン
        /// </summary>
        public string reportFormatVersion;
        /// <summary>
        /// 選択したアバターのルートGameObject名
        /// </summary>
        public string avatarName;
        /// <summary>
        /// 選択したルートGameObjectのHierarchy Path
        /// </summary>
        public string rootScenePath;
        /// <summary>
        /// Unity Editorと導入Packageの環境情報
        /// </summary>
        public EnvironmentReport environment;
        /// <summary>
        /// アバター全体の主要な集計情報
        /// </summary>
        public SummaryReport summary;
        /// <summary>
        /// アバター以下のHierarchy構造
        /// </summary>
        public HierarchyNode hierarchy;
        public List<SkinnedMeshReport> skinnedMeshRenderers = new List<SkinnedMeshReport>();
        public List<StaticMeshReport> staticMeshRenderers = new List<StaticMeshReport>();
        public List<MaterialReport> materials = new List<MaterialReport>();
        public List<TextureReport> textures = new List<TextureReport>();
        public List<AnimatorReport> animators = new List<AnimatorReport>();
        /// <summary>
        /// VRC Avatar DescriptorなどVRChat固有設定の詳細
        /// </summary>
        public VRChatReport vrchat;
        public List<ComponentReport> components = new List<ComponentReport>();
    }

    /// <summary>
    /// Unity EditorとPackageの環境情報
    /// </summary>
    [Serializable]
    public class EnvironmentReport
    {
        /// <summary>
        /// Unity Editorのバージョン
        /// </summary>
        public string unityVersion;
        /// <summary>
        /// Unity Editorを実行しているPlatform
        /// </summary>
        public string platform;
        /// <summary>
        /// 現在のBuild Target
        /// </summary>
        public string activeBuildTarget;
        public List<PackageReport> packages = new List<PackageReport>();
    }

    /// <summary>
    /// Unity Package Manager上の1Packageの情報
    /// </summary>
    [Serializable]
    public class PackageReport
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// Inspector等で表示される名称
        /// </summary>
        public string displayName;
        /// <summary>
        /// versionの比較用値
        /// </summary>
        public string version;
        /// <summary>
        /// sourceの比較用値
        /// </summary>
        public string source;
        /// <summary>
        /// is Direct Dependencyの比較用値
        /// </summary>
        public bool isDirectDependency;
    }

    /// <summary>
    /// アバター全体の主要な集計情報
    /// </summary>
    [Serializable]
    public class SummaryReport
    {
        /// <summary>
        /// ルート以下のGameObject数
        /// </summary>
        public int gameObjectCount;
        /// <summary>
        /// component Countの比較用値
        /// </summary>
        public int componentCount;
        /// <summary>
        /// SkinnedMeshRenderer数
        /// </summary>
        public int skinnedMeshRendererCount;
        /// <summary>
        /// MeshRenderer数
        /// </summary>
        public int staticMeshRendererCount;
        /// <summary>
        /// 重複を除いたMesh数
        /// </summary>
        public int uniqueMeshCount;
        /// <summary>
        /// total Unique Mesh Verticesの比較用値
        /// </summary>
        public long totalUniqueMeshVertices;
        /// <summary>
        /// 重複を除いたMeshの三角形数合計
        /// </summary>
        public long totalUniqueMeshTriangles;
        /// <summary>
        /// SkinnedMeshRendererから参照される重複なしBone数
        /// </summary>
        public int uniqueBoneCount;
        /// <summary>
        /// 全SkinnedMeshRendererに存在するBlendShape数の合計
        /// </summary>
        public int blendShapeCount;
        /// <summary>
        /// material Slot Countの比較用値
        /// </summary>
        public int materialSlotCount;
        /// <summary>
        /// 重複を除いたMaterial数
        /// </summary>
        public int uniqueMaterialCount;
        /// <summary>
        /// Materialから参照される重複なしTexture数
        /// </summary>
        public int uniqueTextureCount;
        /// <summary>
        /// VRC PhysBone Component数
        /// </summary>
        public int physBoneComponentCount;
        /// <summary>
        /// phys Bone Collider Component Countの比較用値
        /// </summary>
        public int physBoneColliderComponentCount;
        /// <summary>
        /// VRC Contact Sender Component数
        /// </summary>
        public int contactSenderComponentCount;
        /// <summary>
        /// VRC Contact Receiver Component数
        /// </summary>
        public int contactReceiverComponentCount;
        public List<ComponentCountReport> componentTypes = new List<ComponentCountReport>();
    }

    /// <summary>
    /// Component型ごとの使用数
    /// </summary>
    [Serializable]
    public class ComponentCountReport
    {
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// countの比較用値
        /// </summary>
        public int count;
    }

    /// <summary>
    /// 1つのGameObjectとその子Hierarchyを表す情報
    /// </summary>
    [Serializable]
    public class HierarchyNode
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// GameObject自身のActive状態
        /// </summary>
        public bool activeSelf;
        /// <summary>
        /// 親階層を含めたGameObjectのActive状態
        /// </summary>
        public bool activeInHierarchy;
        /// <summary>
        /// GameObjectに設定されたTag
        /// </summary>
        public string tag;
        /// <summary>
        /// layerの比較用値
        /// </summary>
        public int layer;
        /// <summary>
        /// GameObjectに設定されたLayer名
        /// </summary>
        public string layerName;
        /// <summary>
        /// TransformのLocal Position
        /// </summary>
        public SerializableVector3 localPosition;
        /// <summary>
        /// TransformのLocal Rotation
        /// </summary>
        public SerializableQuaternion localRotation;
        /// <summary>
        /// local Scaleの比較用値
        /// </summary>
        public SerializableVector3 localScale;
        public List<string> componentTypes = new List<string>();
        public List<HierarchyNode> children = new List<HierarchyNode>();
    }

    /// <summary>
    /// 1つのSkinnedMeshRendererの詳細情報
    /// </summary>
    [Serializable]
    public class SkinnedMeshReport
    {
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// 機能が有効かどうか
        /// </summary>
        public bool enabled;
        /// <summary>
        /// SkinnedMeshRendererのUpdate When Offscreen設定
        /// </summary>
        public bool updateWhenOffscreen;
        /// <summary>
        /// SkinnedMeshRendererのSkinning Quality設定
        /// </summary>
        public string quality;
        /// <summary>
        /// SkinnedMeshRendererのRoot Bone
        /// </summary>
        public ObjectReference rootBone;
        /// <summary>
        /// RendererまたはMeshのBounds情報
        /// </summary>
        public SerializableBounds bounds;
        /// <summary>
        /// Rendererが参照しているMesh情報
        /// </summary>
        public MeshInfo mesh;
        public List<ObjectReference> bones = new List<ObjectReference>();
        public List<ObjectReference> materials = new List<ObjectReference>();
        public List<BlendShapeReport> blendShapes = new List<BlendShapeReport>();
    }

    /// <summary>
    /// 1つのMeshRendererの詳細情報
    /// </summary>
    [Serializable]
    public class StaticMeshReport
    {
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// 機能が有効かどうか
        /// </summary>
        public bool enabled;
        /// <summary>
        /// Rendererが参照しているMesh情報
        /// </summary>
        public MeshInfo mesh;
        public List<ObjectReference> materials = new List<ObjectReference>();
    }

    /// <summary>
    /// Meshの比較用基本情報
    /// </summary>
    [Serializable]
    public class MeshInfo
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// AssetDatabase上のProject内Path
        /// </summary>
        public string assetPath;
        /// <summary>
        /// Meshの頂点数
        /// </summary>
        public int vertexCount;
        /// <summary>
        /// MeshのIndex数合計
        /// </summary>
        public long indexCount;
        /// <summary>
        /// Meshの三角形数
        /// </summary>
        public long triangleCount;
        /// <summary>
        /// sub Mesh Countの比較用値
        /// </summary>
        public int subMeshCount;
        /// <summary>
        /// Meshに含まれるBind Pose数
        /// </summary>
        public int bindPoseCount;
        /// <summary>
        /// 全SkinnedMeshRendererに存在するBlendShape数の合計
        /// </summary>
        public int blendShapeCount;
        /// <summary>
        /// RendererまたはMeshのBounds情報
        /// </summary>
        public SerializableBounds bounds;
    }

    /// <summary>
    /// 1つのBlendShapeの情報
    /// </summary>
    [Serializable]
    public class BlendShapeReport
    {
        /// <summary>
        /// 配列または要素のIndex
        /// </summary>
        public int index;
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// SkinnedMeshRendererに現在設定されているBlendShape Weight
        /// </summary>
        public float currentWeight;
        /// <summary>
        /// BlendShapeのFrame数
        /// </summary>
        public int frameCount;
        public List<BlendShapeFrameReport> frames = new List<BlendShapeFrameReport>();
    }

    /// <summary>
    /// 1つのBlendShape FrameのDelta統計情報
    /// </summary>
    [Serializable]
    public class BlendShapeFrameReport
    {
        /// <summary>
        /// BlendShape FrameのIndex
        /// </summary>
        public int frameIndex;
        /// <summary>
        /// BlendShape Frameに設定されたWeight
        /// </summary>
        public float weight;
        /// <summary>
        /// 位置Deltaが存在する頂点数
        /// </summary>
        public int affectedVertexCount;
        /// <summary>
        /// 法線Deltaが存在する頂点数
        /// </summary>
        public int affectedNormalCount;
        /// <summary>
        /// 接線Deltaが存在する頂点数
        /// </summary>
        public int affectedTangentCount;
        /// <summary>
        /// 位置Deltaの最大Magnitude
        /// </summary>
        public float maxVertexDelta;
        /// <summary>
        /// 影響頂点における位置Deltaの平均Magnitude
        /// </summary>
        public float averageAffectedVertexDelta;
        /// <summary>
        /// 法線Deltaの最大Magnitude
        /// </summary>
        public float maxNormalDelta;
        /// <summary>
        /// 接線Deltaの最大Magnitude
        /// </summary>
        public float maxTangentDelta;
        /// <summary>
        /// 法線Deltaを持つ頂点が存在するかどうか
        /// </summary>
        public bool hasNormalDelta;
        /// <summary>
        /// 接線Deltaを持つ頂点が存在するかどうか
        /// </summary>
        public bool hasTangentDelta;
    }

    /// <summary>
    /// 1つのMaterialとShader設定の情報
    /// </summary>
    [Serializable]
    public class MaterialReport
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// AssetDatabase上のProject内Path
        /// </summary>
        public string assetPath;
        /// <summary>
        /// Materialに設定されているShader名
        /// </summary>
        public string shader;
        /// <summary>
        /// Shader AssetのProject内Path
        /// </summary>
        public string shaderAssetPath;
        /// <summary>
        /// MaterialのRender Queue
        /// </summary>
        public int renderQueue;
        /// <summary>
        /// enable Instancingの比較用値
        /// </summary>
        public bool enableInstancing;
        /// <summary>
        /// Double Sided GI設定
        /// </summary>
        public bool doubleSidedGI;
        /// <summary>
        /// Global Illumination Flags
        /// </summary>
        public string globalIlluminationFlags;
        public List<string> shaderKeywords = new List<string>();
        public List<MaterialPropertyReport> properties = new List<MaterialPropertyReport>();
    }

    /// <summary>
    /// Material内の1つのShader Property情報
    /// </summary>
    [Serializable]
    public class MaterialPropertyReport
    {
        /// <summary>
        /// 配列または要素のIndex
        /// </summary>
        public int index;
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// Shader Propertyの表示用説明
        /// </summary>
        public string description;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// Float、Range、Int系Propertyの値
        /// </summary>
        public float floatValue;
        /// <summary>
        /// color Valueの比較用値
        /// </summary>
        public SerializableColor colorValue;
        /// <summary>
        /// Vector Propertyの値
        /// </summary>
        public SerializableVector4 vectorValue;
        /// <summary>
        /// Texture Propertyが参照するTexture
        /// </summary>
        public ObjectReference texture;
        /// <summary>
        /// Texture PropertyのScale
        /// </summary>
        public SerializableVector2 textureScale;
        /// <summary>
        /// texture Offsetの比較用値
        /// </summary>
        public SerializableVector2 textureOffset;
    }

    /// <summary>
    /// 1つのTextureとImporter設定の情報
    /// </summary>
    [Serializable]
    public class TextureReport
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// AssetDatabase上のProject内Path
        /// </summary>
        public string assetPath;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// Textureの幅
        /// </summary>
        public int width;
        /// <summary>
        /// Textureの高さ
        /// </summary>
        public int height;
        /// <summary>
        /// dimensionの比較用値
        /// </summary>
        public string dimension;
        /// <summary>
        /// TextureのGraphics Format
        /// </summary>
        public string graphicsFormat;
        /// <summary>
        /// TextureのMipmap Level数
        /// </summary>
        public int mipmapCount;
        /// <summary>
        /// TextureのWrap Mode
        /// </summary>
        public string wrapMode;
        /// <summary>
        /// filter Modeの比較用値
        /// </summary>
        public string filterMode;
        /// <summary>
        /// TextureのAnisotropic Filtering Level
        /// </summary>
        public int anisoLevel;
        /// <summary>
        /// TextureImporterの設定
        /// </summary>
        public TextureImporterReport importer;
    }

    /// <summary>
    /// TextureImporterの主要設定
    /// </summary>
    [Serializable]
    public class TextureImporterReport
    {
        /// <summary>
        /// Texture ImporterのTexture Type
        /// </summary>
        public string textureType;
        /// <summary>
        /// texture Shapeの比較用値
        /// </summary>
        public string textureShape;
        /// <summary>
        /// TextureをsRGBとして扱うかどうか
        /// </summary>
        public bool sRGBTexture;
        /// <summary>
        /// Texture ImporterのAlpha Source設定
        /// </summary>
        public string alphaSource;
        /// <summary>
        /// Alpha Is Transparency設定
        /// </summary>
        public bool alphaIsTransparency;
        /// <summary>
        /// mipmap Enabledの比較用値
        /// </summary>
        public bool mipmapEnabled;
        /// <summary>
        /// Streaming Mipmapsの有効状態
        /// </summary>
        public bool streamingMipmaps;
        /// <summary>
        /// Streaming Mipmaps Priority
        /// </summary>
        public int streamingMipmapsPriority;
        /// <summary>
        /// Border Mipmap設定
        /// </summary>
        public bool borderMipmap;
        /// <summary>
        /// mip Maps Preserve Coverageの比較用値
        /// </summary>
        public bool mipMapsPreserveCoverage;
        /// <summary>
        /// Mipmap Fadeout設定
        /// </summary>
        public bool fadeout;
        /// <summary>
        /// TextureのWrap Mode
        /// </summary>
        public string wrapMode;
        /// <summary>
        /// TextureのFilter Mode
        /// </summary>
        public string filterMode;
        /// <summary>
        /// aniso Levelの比較用値
        /// </summary>
        public int anisoLevel;
        /// <summary>
        /// Texture ImporterのMax Texture Size
        /// </summary>
        public int maxTextureSize;
        /// <summary>
        /// Texture Compression設定
        /// </summary>
        public string textureCompression;
        /// <summary>
        /// Texture Compression Quality
        /// </summary>
        public int compressionQuality;
        /// <summary>
        /// crunched Compressionの比較用値
        /// </summary>
        public bool crunchedCompression;
        /// <summary>
        /// Non Power of Two Scale設定
        /// </summary>
        public string npotScale;
        /// <summary>
        /// Read/Write Enabled状態
        /// </summary>
        public bool isReadable;
    }

    /// <summary>
    /// 1つのAnimatorの設定
    /// </summary>
    [Serializable]
    public class AnimatorReport
    {
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// 機能が有効かどうか
        /// </summary>
        public bool enabled;
        /// <summary>
        /// AnimatorのApply Root Motion設定
        /// </summary>
        public bool applyRootMotion;
        /// <summary>
        /// AnimatorのUpdate Mode
        /// </summary>
        public string updateMode;
        /// <summary>
        /// AnimatorのCulling Mode
        /// </summary>
        public string cullingMode;
        /// <summary>
        /// is Humanの比較用値
        /// </summary>
        public bool isHuman;
        /// <summary>
        /// Animatorに設定されているAvatar参照
        /// </summary>
        public ObjectReference avatar;
        /// <summary>
        /// Animatorに設定されているRuntimeAnimatorController参照
        /// </summary>
        public ObjectReference runtimeAnimatorController;
        /// <summary>
        /// Animator Controller内部の詳細情報
        /// </summary>
        public AnimatorControllerReport controller;
    }

    /// <summary>
    /// AnimatorControllerの詳細情報
    /// </summary>
    [Serializable]
    public class AnimatorControllerReport
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// AssetDatabase上のProject内Path
        /// </summary>
        public string assetPath;
        public List<AnimatorParameterReport> parameters = new List<AnimatorParameterReport>();
        public List<AnimatorLayerReport> layers = new List<AnimatorLayerReport>();
    }

    /// <summary>
    /// AnimatorControllerの1Parameter情報
    /// </summary>
    [Serializable]
    public class AnimatorParameterReport
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// Float ParameterのDefault値
        /// </summary>
        public float defaultFloat;
        /// <summary>
        /// Int ParameterのDefault値
        /// </summary>
        public int defaultInt;
        /// <summary>
        /// Bool ParameterのDefault値
        /// </summary>
        public bool defaultBool;
    }

    /// <summary>
    /// AnimatorControllerの1Layer情報
    /// </summary>
    [Serializable]
    public class AnimatorLayerReport
    {
        /// <summary>
        /// 配列または要素のIndex
        /// </summary>
        public int index;
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// Animator LayerのDefault Weight
        /// </summary>
        public float defaultWeight;
        /// <summary>
        /// Animator LayerのBlending Mode
        /// </summary>
        public string blendingMode;
        /// <summary>
        /// Animator LayerのIK Pass設定
        /// </summary>
        public bool iKPass;
        /// <summary>
        /// synced Layer Indexの比較用値
        /// </summary>
        public int syncedLayerIndex;
        /// <summary>
        /// Animator LayerのAvatar Mask参照
        /// </summary>
        public ObjectReference avatarMask;
        public List<AnimatorStateReport> states = new List<AnimatorStateReport>();
    }

    /// <summary>
    /// AnimatorController内の1State情報
    /// </summary>
    [Serializable]
    public class AnimatorStateReport
    {
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// Animator StateのSpeed
        /// </summary>
        public float speed;
        /// <summary>
        /// Animator StateのMirror設定
        /// </summary>
        public bool mirror;
        /// <summary>
        /// Animator StateのWrite Defaults設定
        /// </summary>
        public bool writeDefaultValues;
        /// <summary>
        /// motionの比較用値
        /// </summary>
        public ObjectReference motion;
    }

    /// <summary>
    /// VRChat固有設定をまとめた情報
    /// </summary>
    [Serializable]
    public class VRChatReport
    {
        /// <summary>
        /// avatar Descriptorsの比較用値
        /// </summary>
        public List<VRCAvatarDescriptorReport> avatarDescriptors =
            new List<VRCAvatarDescriptorReport>();
    }

    /// <summary>
    /// VRC Avatar Descriptorの主要設定
    /// </summary>
    [Serializable]
    public class VRCAvatarDescriptorReport
    {
        /// <summary>
        /// ComponentまたはObjectが存在するHierarchy Path
        /// </summary>
        public string gameObjectPath;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// VRC Avatar DescriptorのView Position
        /// </summary>
        public SerializableVector3 viewPosition;
        /// <summary>
        /// Lip Sync設定
        /// </summary>
        public VRCLipSyncReport lipSync;
        /// <summary>
        /// Eye LookおよびEyelid設定
        /// </summary>
        public VRCEyeLookReport eyeLook;
        /// <summary>
        /// Playable Layersの設定
        /// </summary>
        public VRCPlayableLayersReport playableLayers;
        /// <summary>
        /// Expressions MenuとExpression Parametersの設定
        /// </summary>
        public VRCExpressionsReport expressions;
        public List<SerializedPropertyNode> colliders = new List<SerializedPropertyNode>();
        public List<SerializedPropertyNode> rawProperties = new List<SerializedPropertyNode>();
    }

    /// <summary>
    /// VRC Avatar DescriptorのLip Sync設定
    /// </summary>
    [Serializable]
    public class VRCLipSyncReport
    {
        /// <summary>
        /// Lip Sync方式
        /// </summary>
        public string mode;
        /// <summary>
        /// Viseme Blend Shapeに使用するSkinnedMeshRenderer
        /// </summary>
        public ObjectReference visemeSkinnedMesh;
        /// <summary>
        /// Jaw Flap Bone方式で使用するJaw Bone
        /// </summary>
        public ObjectReference jawBone;
        /// <summary>
        /// Jawを閉じた状態の設定値
        /// </summary>
        public string jawClosed;
        /// <summary>
        /// Jawを開いた状態の設定値
        /// </summary>
        public string jawOpen;
        /// <summary>
        /// Mouth Openに使用するBlendShape名
        /// </summary>
        public string mouthOpenBlendShapeName;
        public List<VRCVisemeReport> visemes = new List<VRCVisemeReport>();
    }

    /// <summary>
    /// 1VisemeとBlendShape名の対応情報
    /// </summary>
    [Serializable]
    public class VRCVisemeReport
    {
        /// <summary>
        /// 配列または要素のIndex
        /// </summary>
        public int index;
        /// <summary>
        /// sil、PP、FFなどのViseme名
        /// </summary>
        public string viseme;
        /// <summary>
        /// Visemeに割り当てられたBlendShape名
        /// </summary>
        public string blendShape;
    }

    /// <summary>
    /// VRC Avatar DescriptorのEye Look設定
    /// </summary>
    [Serializable]
    public class VRCEyeLookReport
    {
        /// <summary>
        /// 機能が有効かどうか
        /// </summary>
        public bool enabled;
        public List<SerializedPropertyNode> settings = new List<SerializedPropertyNode>();
    }

    /// <summary>
    /// VRC Avatar DescriptorのPlayable Layers設定
    /// </summary>
    [Serializable]
    public class VRCPlayableLayersReport
    {
        /// <summary>
        /// 標準設定ではなくカスタム設定を使用するかどうか
        /// </summary>
        public bool customized;
        public List<VRCPlayableLayerReport> baseLayers = new List<VRCPlayableLayerReport>();
        public List<VRCPlayableLayerReport> specialLayers = new List<VRCPlayableLayerReport>();
    }

    /// <summary>
    /// VRC Avatar Descriptor上の1Playable Layer情報
    /// </summary>
    [Serializable]
    public class VRCPlayableLayerReport
    {
        /// <summary>
        /// 配列または要素のIndex
        /// </summary>
        public int index;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// VRChat標準Controllerを使用する設定かどうか
        /// </summary>
        public bool isDefault;
        /// <summary>
        /// Playable Layerに割り当てられたAnimator Controller
        /// </summary>
        public ObjectReference animatorController;
        /// <summary>
        /// Animator Controller内部の詳細情報
        /// </summary>
        public AnimatorControllerReport controller;
        public List<SerializedPropertyNode> rawProperties = new List<SerializedPropertyNode>();
    }

    /// <summary>
    /// VRC Avatar DescriptorのExpressions設定
    /// </summary>
    [Serializable]
    public class VRCExpressionsReport
    {
        /// <summary>
        /// 標準設定ではなくカスタム設定を使用するかどうか
        /// </summary>
        public bool customized;
        /// <summary>
        /// Expressions Menu Assetへの参照
        /// </summary>
        public ObjectReference menu;
        /// <summary>
        /// Expression Parameters Assetへの参照
        /// </summary>
        public ObjectReference parameters;
        public List<SerializedPropertyNode> menuProperties = new List<SerializedPropertyNode>();
        public List<SerializedPropertyNode> parameterProperties = new List<SerializedPropertyNode>();
    }

    /// <summary>
    /// 階層構造を保持したSerializedProperty情報
    /// </summary>
    [Serializable]
    public class SerializedPropertyNode
    {
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// Inspector等で表示される名称
        /// </summary>
        public string displayName;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// 比較用に文字列化した値
        /// </summary>
        public string value;
        /// <summary>
        /// SerializedPropertyが配列として扱われるかどうか
        /// </summary>
        public bool isArray;
        /// <summary>
        /// SerializedPropertyが配列の場合の要素数
        /// </summary>
        public int arraySize;
        /// <summary>
        /// Unity Objectへの参照情報
        /// </summary>
        public ObjectReference objectReference;
        public List<SerializedPropertyNode> children = new List<SerializedPropertyNode>();
    }

    /// <summary>
    /// 1つのComponentとSerializedProperty一覧
    /// </summary>
    [Serializable]
    public class ComponentReport
    {
        /// <summary>
        /// ComponentまたはObjectが存在するHierarchy Path
        /// </summary>
        public string gameObjectPath;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// Component型を含むAssembly名
        /// </summary>
        public string assembly;
        /// <summary>
        /// 設定値の読取時に発生したエラー内容
        /// </summary>
        public string readError;
        /// <summary>
        /// 取得したProperty一覧
        /// </summary>
        public List<SerializedPropertyReport> properties =
            new List<SerializedPropertyReport>();
    }

    /// <summary>
    /// 1つのSerializedPropertyの比較用情報
    /// </summary>
    [Serializable]
    public class SerializedPropertyReport
    {
        /// <summary>
        /// 対象ObjectまたはPropertyのPath
        /// </summary>
        public string path;
        /// <summary>
        /// Inspector等で表示される名称
        /// </summary>
        public string displayName;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// 比較用に文字列化した値
        /// </summary>
        public string value;
        /// <summary>
        /// Unity Objectへの参照情報
        /// </summary>
        public ObjectReference objectReference;
    }

    /// <summary>
    /// Unity Objectを比較しやすく表した参照情報
    /// </summary>
    [Serializable]
    public class ObjectReference
    {
        /// <summary>
        /// 対象の名前
        /// </summary>
        public string name;
        /// <summary>
        /// 対象の型または種別
        /// </summary>
        public string type;
        /// <summary>
        /// AssetDatabase上のProject内Path
        /// </summary>
        public string assetPath;
        /// <summary>
        /// Scene内または選択ルート以下のHierarchy Path
        /// </summary>
        public string hierarchyPath;
    }

    /// <summary>
    /// JSONへシリアライズ可能なVector2
    /// </summary>
    [Serializable]
    public class SerializableVector2
    {
        /// <summary>
        /// X成分
        /// </summary>
        public float x;
        /// <summary>
        /// yの比較用値
        /// </summary>
        public float y;

        public static SerializableVector2 From(Vector2 value)
            => new SerializableVector2 { x = value.x, y = value.y };
    }

    /// <summary>
    /// JSONへシリアライズ可能なVector3
    /// </summary>
    [Serializable]
    public class SerializableVector3
    {
        /// <summary>
        /// X成分
        /// </summary>
        public float x;
        /// <summary>
        /// yの比較用値
        /// </summary>
        public float y;
        /// <summary>
        /// Z成分
        /// </summary>
        public float z;

        public static SerializableVector3 From(Vector3 value)
            => new SerializableVector3 { x = value.x, y = value.y, z = value.z };
    }

    /// <summary>
    /// JSONへシリアライズ可能なVector4
    /// </summary>
    [Serializable]
    public class SerializableVector4
    {
        /// <summary>
        /// X成分
        /// </summary>
        public float x;
        /// <summary>
        /// yの比較用値
        /// </summary>
        public float y;
        /// <summary>
        /// Z成分
        /// </summary>
        public float z;
        /// <summary>
        /// W成分
        /// </summary>
        public float w;

        public static SerializableVector4 From(Vector4 value)
            => new SerializableVector4
            {
                x = value.x,
                y = value.y,
                z = value.z,
                w = value.w
            };
    }

    /// <summary>
    /// JSONへシリアライズ可能なQuaternion
    /// </summary>
    [Serializable]
    public class SerializableQuaternion
    {
        /// <summary>
        /// X成分
        /// </summary>
        public float x;
        /// <summary>
        /// yの比較用値
        /// </summary>
        public float y;
        /// <summary>
        /// Z成分
        /// </summary>
        public float z;
        /// <summary>
        /// W成分
        /// </summary>
        public float w;

        public static SerializableQuaternion From(Quaternion value)
            => new SerializableQuaternion
            {
                x = value.x,
                y = value.y,
                z = value.z,
                w = value.w
            };
    }

    /// <summary>
    /// JSONへシリアライズ可能なColor
    /// </summary>
    [Serializable]
    public class SerializableColor
    {
        /// <summary>
        /// Red成分
        /// </summary>
        public float r;
        /// <summary>
        /// gの比較用値
        /// </summary>
        public float g;
        /// <summary>
        /// Blue成分
        /// </summary>
        public float b;
        /// <summary>
        /// Alpha成分
        /// </summary>
        public float a;

        public static SerializableColor From(Color value)
            => new SerializableColor
            {
                r = value.r,
                g = value.g,
                b = value.b,
                a = value.a
            };
    }

    /// <summary>
    /// JSONへシリアライズ可能なBounds
    /// </summary>
    [Serializable]
    public class SerializableBounds
    {
        /// <summary>
        /// Boundsの中心座標
        /// </summary>
        public SerializableVector3 center;
        /// <summary>
        /// sizeの比較用値
        /// </summary>
        public SerializableVector3 size;

        public static SerializableBounds From(Bounds value)
            => new SerializableBounds
            {
                center = SerializableVector3.From(value.center),
                size = SerializableVector3.From(value.size)
            };
    }
}
