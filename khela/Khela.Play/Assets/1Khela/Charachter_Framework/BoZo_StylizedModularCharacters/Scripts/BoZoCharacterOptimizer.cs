using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.Rendering;
using System.Threading.Tasks;
using System;

namespace Bozo.ModularCharacters
{
    public class BoZo_CharacterOptimizer
    {
        private static string path = "/BoZo_StylizedModularCharacters/CustomCharacters/Prefabs";

        private MergedMaterialData[] mergedMaterialDatas;
        private OutfitSystem sourceSystem;

        public async Task<GameObject> OptimizeCharacter(OutfitSystem source, CharacterData data, bool save = false)
        {
            if (source == null) return null;
            if (source.mergeMaterial == null)
            {
                Debug.Log("Merge Material required, please assign one in the inspector");
                return null;
            }

            sourceSystem = source;
            var body = source;

            //creating clean base to work on
            if (false)
            {
                body = await PrepareMergeBase(source, data);
                mergedMaterialDatas = source.materialData;
            }

            body.transform.position = Vector3.zero;
            var mergedBody = await Merge(body, save);



            source.customMaps = body.customMaps;

            return mergedBody;
        }

        private async Task<OutfitSystem> PrepareMergeBase(OutfitSystem source, CharacterData data)
        {
            var characterBaseOb = Resources.Load<OutfitSystem>("BMAC_MergedCharacterBase");
            var body = UnityEngine.Object.Instantiate(characterBaseOb, new Vector3(0, -0, 0), Quaternion.identity);
            //body.mergeBase = true;
            body.MuteHeightChange(true);
            BMAC_SaveSystem.LoadCharacter(body, data, true, true);

            body.mergeMaterial = source.mergeMaterial;

            return body;
        }

        public void MergeButTheBetterOne(OutfitSystem outfitSystem)
        {
            var body = outfitSystem.GetCharacterBody();
            var outfitsToMerge = outfitSystem.GetOutfits();
            List<Renderer> rendererList = new List<Renderer>();

            var rootBone = body.rootBone;

            //Creating Bone Map
            var boneMap = new Dictionary<string, boneData>();
            var boneList = outfitSystem.GetBones().Values.ToList();

            for (int i = 0; i < boneList.Count; i++)
            {
                try
                {
                    var data = new boneData();
                    data.bone = boneList[i];
                    data.index = i;

                    boneMap.Add(boneList[i].name, data);
                }
                catch
                {
                    Debug.LogError("Missing Bone In Bone Map");
                    //return;
                }
            }


            //Mesh Data Initaliztion
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector4> tangents = new List<Vector4>();
            List<Vector2> uv = new List<Vector2>();
            List<Color> colors = new List<Color>();
            List<BoneWeight> boneWeights = new List<BoneWeight>();
            List<Matrix4x4> bindposes = new List<Matrix4x4>();
            List<List<int>> submeshTriangles = new List<List<int>>();
            List<Material> materials = new List<Material>();

            materials.Add(outfitSystem.GetCharacterBody().sharedMaterial);
            List<string> finalBlendshapeOrder = new List<string>();
            Dictionary<string, List<BlendshapeData>> blendshapeGroups = new Dictionary<string, List<BlendshapeData>>();
            int vertexOffset = 0;

            submeshTriangles.Add(new List<int>());

            try
            {

                foreach (var item in outfitsToMerge)
                {
                    if (item == null) continue;
                    if (!item.mergeMesh) continue;
                    if (outfitSystem.hiddenTypes.ContainsKey(item.Type)) continue;

                    foreach (var r in item.skinnedRenderers)
                    {
                        if (r.gameObject.activeSelf) rendererList.Add(r);
                    }

                    //Stopping Animators
                    var anim = item.GetComponentInChildren<Animator>();
                    if (anim != null)
                    {
                        anim.enabled = false;
                        anim.Rebind();
                    }

                    item.gameObject.SetActive(false);
                }
            }
            catch
            {
                Debug.LogError("Mesh Merge Initalization failed");
                return;
            }



            try
            {
                #region ------------------ Main Merge Loop ------------------
                for (int i = 0; i < rendererList.Count; i++)
                {
                    var outfit = rendererList[i].GetComponentInParent<Outfit>(true);
                    var smr = rendererList[i].GetComponentInChildren<SkinnedMeshRenderer>(true);
                    Dictionary<int, int> boneIndexMap = new Dictionary<int, int>();

                    try
                    {
                        #region Mapping Bones


                        Transform[] meshBones = smr.bones;
                        if (smr.rootBone != null)
                        {
                            // Build remap table: mesh bone index -> master bone index
                            for (int b = 0; b < meshBones.Length; b++)
                            {
                                Transform bone = meshBones[b];
                                int masterIndex = -1;
                                if (bone == null) continue;
                                if (boneMap.ContainsKey(bone.name))
                                {

                                    masterIndex = boneMap[bone.name].index;
                                }
                                else
                                {
                                    Debug.LogWarning(bone.name + " Is not in BoneMap");
                                }

                                if (masterIndex == -1)
                                {
                                    continue;
                                }

                                boneIndexMap[b] = masterIndex;
                            }
                        }
                        #endregion
                    }
                    catch
                    {
                        Debug.LogError("Bone Mapping failed");
                    }


                    #region meshVertices

                    Mesh mesh = smr.sharedMesh;

                    // Convert from this renderer's mesh local space into the final body's local space.
                    // This is correct even if the outfit object is offset/rotated/scaled differently.
                    Matrix4x4 meshToBodyLocal = body.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;

                    // For normals/tangents, use vector transform, not point transform.
                    // If you have weird non-uniform scaling, this is safer for normals.
                    Matrix4x4 normalToBodyLocal =
                        meshToBodyLocal.inverse.transpose;

                    // Source mesh data
                    Vector3[] meshVertices = mesh.vertices;
                    Vector3[] meshNormals = mesh.normals;
                    Vector4[] meshTangents = mesh.tangents;

                    // Transformed mesh data
                    Vector3[] transformedVertices = new Vector3[mesh.vertexCount];
                    Vector3[] transformedNormals = new Vector3[mesh.vertexCount];
                    Vector4[] transformedTangents = new Vector4[mesh.vertexCount];

                    for (int v = 0; v < mesh.vertexCount; v++)
                    {
                        // Positions are points, so use MultiplyPoint3x4.
                        transformedVertices[v] =
                            meshToBodyLocal.MultiplyPoint3x4(meshVertices[v]);

                        // Normals are directions, so use MultiplyVector.
                        if (meshNormals != null && meshNormals.Length == mesh.vertexCount)
                        {
                            transformedNormals[v] =
                                normalToBodyLocal.MultiplyVector(meshNormals[v]).normalized;
                        }
                        else
                        {
                            transformedNormals[v] = Vector3.up;
                        }

                        // Tangents are also directions.
                        if (meshTangents != null && meshTangents.Length == mesh.vertexCount)
                        {
                            Vector3 tangent = new Vector3(
                                meshTangents[v].x,
                                meshTangents[v].y,
                                meshTangents[v].z
                            );

                            tangent = meshToBodyLocal.MultiplyVector(tangent).normalized;

                            transformedTangents[v] = new Vector4(
                                tangent.x,
                                tangent.y,
                                tangent.z,
                                meshTangents[v].w
                            );
                        }
                        else
                        {
                            transformedTangents[v] = new Vector4(1, 0, 0, 1);
                        }
                    }

                    // Append vertex attributes
                    vertices.AddRange(transformedVertices);
                    normals.AddRange(transformedNormals);
                    tangents.AddRange(transformedTangents);

                    #endregion

                    #region UV
                    uv.AddRange(mesh.uv);
                    #endregion

                    #region Vertex colors
                    // Vertex colors
                    Color[] meshColors = mesh.colors;
                    if (meshColors != null && meshColors.Length == mesh.vertexCount)
                    {
                        colors.AddRange(meshColors);
                    }
                    else
                    {
                        // Fill with red if missing
                        for (int c = 0; c < mesh.vertexCount; c++)
                            colors.Add(Color.red);
                    }
                    #endregion

                    try
                    {
                        #region Remap bone weights

                        BoneWeight[] meshBoneWeights = mesh.boneWeights;

                        int fallbackBoneIndex = boneIndexMap[boneMap[smr.rootBone.name].index];

                        foreach (BoneWeight bw in meshBoneWeights)
                        {
                            BoneWeight newBw = new BoneWeight();

                            newBw.boneIndex0 = boneIndexMap.TryGetValue(bw.boneIndex0, out int b0) ? b0 : fallbackBoneIndex;
                            newBw.boneIndex1 = boneIndexMap.TryGetValue(bw.boneIndex1, out int b1) ? b1 : fallbackBoneIndex;
                            newBw.boneIndex2 = boneIndexMap.TryGetValue(bw.boneIndex2, out int b2) ? b2 : fallbackBoneIndex;
                            newBw.boneIndex3 = boneIndexMap.TryGetValue(bw.boneIndex3, out int b3) ? b3 : fallbackBoneIndex;

                            newBw.weight0 = bw.weight0;
                            newBw.weight1 = bw.weight1;
                            newBw.weight2 = bw.weight2;
                            newBw.weight3 = bw.weight3;

                            boneWeights.Add(newBw);
                        }

                        #endregion
                    }
                    catch
                    {
                        Debug.LogError("bone remap failed");
                        return;
                    }

                    #region Sub Meshes

                    var submesh = submeshTriangles[0];
                    if (!outfit.mergeTexture)
                    {
                        submeshTriangles.Add(new List<int>());
                        submesh = submeshTriangles[submeshTriangles.Count - 1];
                        materials.Add(outfit.material);
                    }

                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        List<int> triangles = mesh.GetTriangles(s).ToList();
                        for (int t = 0; t < triangles.Count; t++)
                        {
                            triangles[t] += vertexOffset;
                        }

                        submesh.AddRange(triangles);
                    }
                    #endregion

                    #region Blend Shapes
                    int blendshapeCount = mesh.blendShapeCount;
                    for (int b = 0; b < blendshapeCount; b++)
                    {
                        string shapeName = mesh.GetBlendShapeName(b);
                        var split = shapeName.Split(".");
                        if (split.Length > 0)
                        {
                            shapeName = shapeName.Replace(split[0] + ".", "");
                        }
                        int frameCount = mesh.GetBlendShapeFrameCount(b);

                        for (int f = 0; f < frameCount; f++)
                        {
                            float weight = mesh.GetBlendShapeFrameWeight(b, f);
                            float currentWeight = smr.GetBlendShapeWeight(b);

                            Vector3[] deltaVertices = new Vector3[mesh.vertexCount];
                            Vector3[] deltaNormals = new Vector3[mesh.vertexCount];
                            Vector3[] deltaTangents = new Vector3[mesh.vertexCount];

                            mesh.GetBlendShapeFrameVertices(b, f, deltaVertices, deltaNormals, deltaTangents);

                            if (!blendshapeGroups.ContainsKey(shapeName))
                                blendshapeGroups[shapeName] = new List<BlendshapeData>();
                            blendshapeGroups[shapeName].Add(new BlendshapeData
                            {
                                name = shapeName,
                                weight = weight,
                                currentWeight = currentWeight,
                                deltaVertices = deltaVertices,
                                deltaNormals = deltaNormals,
                                deltaTangents = deltaTangents,
                                vertexOffset = vertexOffset
                            });
                        }
                    }
                    #endregion

                    vertexOffset += mesh.vertexCount;
                }
                #endregion
            }
            catch
            {
                Debug.LogError("Merge Loop has failed");
                return;
            }

            bindposes.Clear();
            //Bind Pose Merge
            for (int i = 0; i < boneList.Count; i++)
            {
                bindposes.Add(boneList[i].worldToLocalMatrix * body.transform.localToWorldMatrix);
            }

            try
            {
                // Build combined mesh
                if (outfitSystem.combinedMesh != null) UnityEngine.Object.Destroy(outfitSystem.combinedMesh);
                var combinedMesh = new Mesh();

                outfitSystem.combinedMesh = combinedMesh;

                combinedMesh.name = "MergedMesh";
                combinedMesh.SetVertices(vertices);
                combinedMesh.SetNormals(normals);
                combinedMesh.SetTangents(tangents);
                combinedMesh.SetUVs(0, uv);
                combinedMesh.SetColors(colors);
                combinedMesh.boneWeights = boneWeights.ToArray();
                combinedMesh.bindposes = bindposes.ToArray();
                combinedMesh.subMeshCount = submeshTriangles.Count;

                //Creating Material Regions
                for (int i = 0; i < submeshTriangles.Count; i++)
                {
                    combinedMesh.SetTriangles(submeshTriangles[i], i);
                }

                body.sharedMesh = combinedMesh;
                body.bones = boneList.ToArray();
                body.rootBone = rootBone;
                body.sharedMaterials = materials.ToArray();


                #region BlendShapes
                // Create merged blendshapes
                foreach (string shapeName in blendshapeGroups.Keys.OrderBy(x => x))
                {
                    List<BlendshapeData> entries = blendshapeGroups[shapeName];

                    float frameWeight = entries[0].weight;
                    float currentWeight = 0;

                    int totalVertices = combinedMesh.vertexCount;
                    Vector3[] mergedDeltaVertices = new Vector3[totalVertices];
                    Vector3[] mergedDeltaNormals = new Vector3[totalVertices];
                    Vector3[] mergedDeltaTangents = new Vector3[totalVertices];

                    foreach (var e in entries)
                    {
                        for (int i = 0; i < e.deltaVertices.Length; i++)
                        {
                            int index = e.vertexOffset + i;

                            mergedDeltaVertices[index] = e.deltaVertices[i];
                            mergedDeltaNormals[index] = e.deltaNormals[i];
                            mergedDeltaTangents[index] = e.deltaTangents[i];
                        }

                        currentWeight = Mathf.Max(currentWeight, e.currentWeight);
                    }

                    combinedMesh.AddBlendShapeFrame(
                        shapeName,
                        frameWeight,
                        mergedDeltaVertices,
                        mergedDeltaNormals,
                        mergedDeltaTangents
                    );

                    int blendIndex = combinedMesh.GetBlendShapeIndex(shapeName);
                    if (blendIndex >= 0)
                        body.SetBlendShapeWeight(blendIndex, currentWeight);
                }
                #endregion

                Bounds newBounds = new Bounds(Vector3.zero, Vector3.zero);
                newBounds.Encapsulate(combinedMesh.bounds);
                body.localBounds = newBounds;

                body.gameObject.SetActive(true);

            }
            catch
            {
                Debug.Log("Mesh Creation Failed");
            }
        }


        public async Task<GameObject> Merge(OutfitSystem outfitSystem, bool saveAsPrefab = false)
        {
            if (!Application.isPlaying) return null;

            var outfitsToMerge = outfitSystem.GetOutfits();
            var rig = outfitSystem.GetCharacterBody();
            var materialBase = outfitSystem.mergeMaterial;


            #region ----- Initalization -----

            if (outfitsToMerge == null || outfitsToMerge.Count == 0)
            {
                Debug.LogError("No Skinned Mesh Renderers assigned.");
                return null;
            }

            //Initalizing Bones
            var masterBones = outfitSystem.GetBones().Values.ToList();
            var rootBone = rig.rootBone;
            var parent = rig.transform.parent;

            //Creating Map
            Dictionary<string, boneData> boneMap = new Dictionary<string, boneData>();

            for (int i = 0; i < masterBones.Count; i++)
            {
                var data = new boneData();
                data.bone = masterBones[i];
                data.index = i;

                boneMap.Add(masterBones[i].name, data);
            }

            bool MergeMaterials = false;

            //Mesh Data Initaliztion
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector4> tangents = new List<Vector4>();
            List<Vector2> uv = new List<Vector2>();
            List<Vector2> uv2 = new List<Vector2>();
            List<Color> colors = new List<Color>();
            List<BoneWeight> boneWeights = new List<BoneWeight>();
            List<Material> materials = new List<Material>();
            List<Matrix4x4> bindposes = new List<Matrix4x4>();
            Dictionary<string, List<BlendshapeData>> blendshapeGroups = new Dictionary<string, List<BlendshapeData>>();
            int vertexOffset = 0;
            List<List<int>> submeshTriangles = new List<List<int>>();
            int maxMaterialIndex = -999;

            var mergedTexture = new Texture2D(2, 2);
            var atlasTransform = new Dictionary<string, Rect>();

            Material newMaterial = new Material(materialBase);
            newMaterial.mainTexture = null;
            List<Renderer> rendererList = new List<Renderer>();

            foreach (var item in outfitsToMerge)
            {
                if (outfitSystem.hiddenTypes.ContainsKey(item.Type)) continue;
                rendererList.AddRange(item.GetComponentsInChildren<Renderer>());
                var anim = item.GetComponentInChildren<Animator>();
                if (anim != null) anim.enabled = false;
                if (item.materialIndex > maxMaterialIndex) maxMaterialIndex = item.materialIndex;

                //Gather CustomMaps
                var ext = item.GetComponent<IOutfitExtension<Texture>>();
                if (ext != null)
                {
                    sourceSystem.customMaps[ext.GetID()] = ext.GetValue();
                }
            }

            foreach (var item in rendererList)
            {
                item.enabled = (false);
            }

            var mats = PackTextures(outfitSystem);
            var materialMap = new Dictionary<string, int>();

            for (int i = 0; i < mats.Count; i++)
            {
                materialMap[mats[i].name] = i;
            }

            for (int i = 0; i < materialMap.Count + 1; i++)
            {
                submeshTriangles.Add(new List<int>());
            }

            //Packing Texture
            if (mats.Count == 0)
            {
                //packedTextures = await CreateMergedTextures(outfitsToMerge);
                //newMaterial.mainTexture = packedTextures.texture;

                foreach (var item in mergedMaterialDatas)
                {
                    //newMaterial.SetTexture(item.toMateiralProperty, packedTextures.additionalMaps[item.toMateiralProperty]);
                }
                //atlasTransform = packedTextures.rect;
                //outfitSystem.customMaps = packedTextures.customMaps;
            }



            #endregion

            Dictionary<int, int> boneIndexMap = new Dictionary<int, int>();

            #region ------------------ Main Merge Loop ------------------

            for (int i = 0; i < rendererList.Count; i++)
            {
                var smr = rendererList[i].GetComponentInChildren<SkinnedMeshRenderer>(true);
                var outfit = rendererList[i].GetComponentInParent<Outfit>(true);

                //Not SkinnedMesh converting to SkinnedMesh
                if (smr == null)
                {
                    Mesh staticMesh = rendererList[i].GetComponentInChildren<MeshFilter>(true).sharedMesh;
                    var meshRenderer = rendererList[i].GetComponentInChildren<MeshRenderer>(true);
                    var staticMaterial = meshRenderer.sharedMaterial;
                    var meshGameObject = meshRenderer.gameObject;
                    UnityEngine.Object.DestroyImmediate(meshRenderer);
                    UnityEngine.Object.DestroyImmediate(meshGameObject.GetComponent<MeshFilter>());
                    smr = meshGameObject.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMaterial = staticMaterial;
                    smr.sharedMesh = staticMesh;
                }



                Mesh mesh = smr.sharedMesh;
                var boneIndexOffset = 0;
                bool hasSkeleton = true;

                //Check if its the same skeleton
                if (smr.rootBone != null)
                {
                    if (!boneMap.ContainsKey(smr.rootBone.name))
                    {
                        boneIndexOffset = masterBones.Count;

                        if (outfit.AttachPoint == "")
                        {
                            Debug.Log("What are you doing here? Stop the show...");
                            return null;
                        }


                        smr.rootBone = boneMap[outfit.AttachPoint].bone;
                        masterBones.AddRange(smr.bones);
                        smr.bones[0].SetParent(boneMap[outfit.AttachPoint].bone);

                        for (int b = 0; b < smr.bones.Length; b++)
                        {
                            var dupCounter = 1;

                            boneData data = new boneData();
                            data.bone = smr.bones[b];
                            data.index = b + boneIndexOffset;

                            try
                            {
                                boneMap.Add(smr.bones[b].name, data);
                            }
                            catch
                            {
                                Debug.LogWarning($"Duplicate bone naming in: <{outfit.name}> <{smr.bones[b].name}>Please give bone a unique name");
                                boneMap.Add(smr.bones[b].name + dupCounter, data);
                            }
                        }
                    }
                }

                // Collect transforms in this mesh
                Transform[] meshBones = smr.bones;

                if (smr.rootBone != null)
                {
                    // Build remap table: mesh bone index -> master bone index
                    for (int b = 0; b < meshBones.Length; b++)
                    {
                        Transform bone = meshBones[b];
                        int masterIndex = -1;
                        if (bone == null) continue;
                        if (boneMap.ContainsKey(bone.name))
                        {
                            masterIndex = boneMap[bone.name].index;
                        }
                        else
                        {
                            Debug.LogWarning(bone.name + " Is not in BoneMap");
                        }

                        if (masterIndex == -1)
                        {
                            continue;
                        }
                        boneIndexMap[b] = masterIndex;
                    }
                }
                else
                {
                    hasSkeleton = false;
                    int masterIndex = -1;
                    masterIndex = boneMap[outfit.AttachPoint].index;
                    boneIndexMap[0] = masterIndex;
                    smr.bones = masterBones.ToArray();
                    smr.rootBone = rootBone;
                }


                #region meshVertices

                // Bake transform into vertices/normals/tangents
                Matrix4x4 localToWorld = smr.transform.localToWorldMatrix;

                // Transform vertices
                Vector3[] transformedVertices = new Vector3[mesh.vertexCount];
                Vector3[] transformedNormals = new Vector3[mesh.vertexCount];
                Vector4[] transformedTangents = new Vector4[mesh.vertexCount];

                Vector3[] meshVertices = mesh.vertices;
                Vector3[] meshNormals = mesh.normals;
                Vector4[] meshTangents = mesh.tangents;


                for (int v = 0; v < mesh.vertexCount; v++)
                {
                    transformedVertices[v] = localToWorld.MultiplyPoint3x4(meshVertices[v]);
                    transformedNormals[v] = localToWorld.MultiplyVector(meshNormals[v]).normalized;

                    // Tangents need special handling (w is the handedness)
                    Vector3 t = localToWorld.MultiplyVector(new Vector3(meshTangents[v].x, meshTangents[v].y, meshTangents[v].z)).normalized;
                    transformedTangents[v] = new Vector4(t.x, t.y, t.z, meshTangents[v].w);
                }


                // Append vertex attributes
                vertices.AddRange(transformedVertices);
                normals.AddRange(transformedNormals);
                tangents.AddRange(transformedTangents);



                #endregion

                if (MergeMaterials)
                {
                    Vector2[] originalUVs = mesh.uv;
                    Vector2[] remappedUVs = new Vector2[originalUVs.Length];

                    Rect rect = atlasTransform[outfit.name];


                    for (int u = 0; u < originalUVs.Length; u++)
                    {
                        Vector2 uvVert = originalUVs[u];
                        remappedUVs[u] = new Vector2(
                            rect.x + uvVert.x * rect.width,
                            rect.y + uvVert.y * rect.height
                        );
                    }

                    uv.AddRange(remappedUVs);
                }
                else
                {
                    //UV merging
                    uv.AddRange(mesh.uv);

                    if (mesh.uv2 != null && mesh.uv2.Length == mesh.vertexCount)
                    {
                        // This mesh has valid UV2s
                        uv2.AddRange(mesh.uv2);
                    }
                    else
                    {
                        Vector2[] defaultUV2 = new Vector2[mesh.vertexCount];
                        uv2.AddRange(defaultUV2);
                        // This mesh has no UV2s
                    }
                }

                // Vertex colors
                Color[] meshColors = mesh.colors;
                if (meshColors != null && meshColors.Length == mesh.vertexCount)
                {
                    colors.AddRange(meshColors);
                }
                else
                {
                    // Fill with red if missing
                    for (int c = 0; c < mesh.vertexCount; c++)
                        colors.Add(Color.red);
                }




                if (hasSkeleton)
                {
                    // Remap bone weights
                    BoneWeight[] meshBoneWeights = mesh.boneWeights;
                    foreach (BoneWeight bw in meshBoneWeights)
                    {
                        //Debug.Log($"{bw.boneIndex0} {bw.boneIndex1} {bw.boneIndex2} {bw.boneIndex3}");
                        if (!boneIndexMap.ContainsKey(bw.boneIndex0))
                        {
                            BoneWeight newBw = new BoneWeight
                            {
                                boneIndex0 = boneIndexMap[boneMap[smr.rootBone.name].index],
                                weight0 = 1,
                            };
                            boneWeights.Add(newBw);
                        }
                        else
                        {
                            BoneWeight newBw = new BoneWeight
                            {
                                boneIndex0 = boneIndexMap[bw.boneIndex0],
                                boneIndex1 = boneIndexMap[bw.boneIndex1],
                                boneIndex2 = boneIndexMap[bw.boneIndex2],
                                boneIndex3 = boneIndexMap[bw.boneIndex3],
                                weight0 = bw.weight0,
                                weight1 = bw.weight1,
                                weight2 = bw.weight2,
                                weight3 = bw.weight3
                            };
                            boneWeights.Add(newBw);
                        }
                    }
                }
                else
                {
                    //Has no weights and is being assigned them
                    BoneWeight[] meshBoneWeights = new BoneWeight[mesh.vertexCount];
                    foreach (BoneWeight bw in meshBoneWeights)
                    {
                        BoneWeight newBw = new BoneWeight
                        {
                            boneIndex0 = boneIndexMap[bw.boneIndex0],
                            weight0 = 1,
                        };
                        boneWeights.Add(newBw);
                    }
                }

                #region Submeshes and materials

                int matIndex = -999;
                matIndex = outfit.materialIndex;
                if (outfit.materialIndex == -1) matIndex = materialMap[outfit.Type.name];

                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    List<int> triangles = mesh.GetTriangles(s).ToList();
                    for (int t = 0; t < triangles.Count; t++)
                    {
                        triangles[t] += vertexOffset;
                    }

                    submeshTriangles[matIndex].AddRange(triangles);
                }

                #endregion

                // Extract blendshapes
                int blendshapeCount = mesh.blendShapeCount;
                for (int b = 0; b < blendshapeCount; b++)
                {
                    string shapeName = mesh.GetBlendShapeName(b);
                    var split = shapeName.Split(".");
                    if (split.Length > 0)
                    {
                        shapeName = shapeName.Replace(split[0] + ".", "");
                    }
                    int frameCount = mesh.GetBlendShapeFrameCount(b);

                    for (int f = 0; f < frameCount; f++)
                    {
                        float weight = mesh.GetBlendShapeFrameWeight(b, f);
                        float currentWeight = smr.GetBlendShapeWeight(b);

                        Vector3[] deltaVertices = new Vector3[mesh.vertexCount];
                        Vector3[] deltaNormals = new Vector3[mesh.vertexCount];
                        Vector3[] deltaTangents = new Vector3[mesh.vertexCount];

                        mesh.GetBlendShapeFrameVertices(b, f, deltaVertices, deltaNormals, deltaTangents);

                        if (!blendshapeGroups.ContainsKey(shapeName))
                            blendshapeGroups[shapeName] = new List<BlendshapeData>();
                        blendshapeGroups[shapeName].Add(new BlendshapeData
                        {
                            name = shapeName,
                            weight = weight,
                            currentWeight = currentWeight,
                            deltaVertices = deltaVertices,
                            deltaNormals = deltaNormals,
                            deltaTangents = deltaTangents,
                            vertexOffset = vertexOffset
                        });
                    }
                }


                vertexOffset += mesh.vertexCount;
            }


            foreach (var item in outfitsToMerge)
            {
                UnityEngine.Object.DestroyImmediate(item.gameObject);
            }

            UnityEngine.Object.DestroyImmediate(rig.gameObject);



            #endregion

            #region ------ Mesh Creation -----



            //Bind Pose Merge
            for (int i = 0; i < masterBones.Count; i++)
            {
                bindposes.Add(masterBones[i].worldToLocalMatrix * rootBone.localToWorldMatrix);
            }


            // Build combined mesh
            Mesh combinedMesh = new Mesh();
            combinedMesh.name = "Combined Skinned Mesh";
            combinedMesh.SetVertices(vertices);
            combinedMesh.SetNormals(normals);
            combinedMesh.SetTangents(tangents);
            combinedMesh.SetUVs(0, uv);
            combinedMesh.SetUVs(1, uv2);
            combinedMesh.SetColors(colors);
            combinedMesh.boneWeights = boneWeights.ToArray();
            combinedMesh.bindposes = bindposes.ToArray();
            combinedMesh.subMeshCount = submeshTriangles.Count;



            //Creating Material Regions
            for (int i = 0; i < submeshTriangles.Count; i++)
            {
                combinedMesh.SetTriangles(submeshTriangles[i], i);
            }

            #region BlendShapes
            // Create merged blendshapes
            foreach (var kv in blendshapeGroups)
            {
                string shapeName = kv.Key;
                List<BlendshapeData> entries = kv.Value;

                // Assuming all frames have the same weight
                float frameWeight = entries[0].weight;

                // Prepare deltas for the combined mesh size
                int totalVertices = combinedMesh.vertexCount;
                Vector3[] mergedDeltaVertices = new Vector3[totalVertices];
                Vector3[] mergedDeltaNormals = new Vector3[totalVertices];
                Vector3[] mergedDeltaTangents = new Vector3[totalVertices];

                foreach (var e in entries)
                {
                    for (int i = 0; i < e.deltaVertices.Length; i++)
                    {
                        mergedDeltaVertices[e.vertexOffset + i] = e.deltaVertices[i];
                        mergedDeltaNormals[e.vertexOffset + i] = e.deltaNormals[i];
                        mergedDeltaTangents[e.vertexOffset + i] = e.deltaTangents[i];
                    }
                }

                combinedMesh.AddBlendShapeFrame(
                    shapeName,
                    frameWeight,
                    mergedDeltaVertices,
                    mergedDeltaNormals,
                    mergedDeltaTangents
                );
            }
            #endregion

            #region Final GameObject
            // Create new GameObject
            GameObject mergedGO = new GameObject("CombinedSkinnedMesh");

            SkinnedMeshRenderer newRenderer = mergedGO.AddComponent<SkinnedMeshRenderer>();
            newRenderer.sharedMesh = combinedMesh;
            newRenderer.bones = masterBones.ToArray();
            newRenderer.rootBone = rootBone;

            if (false)
            {
                newRenderer.material = newMaterial;
            }
            else
            {
                newRenderer.materials = mats.ToArray();
            }

            foreach (var key in blendshapeGroups.Keys)
            {
                var blendData = blendshapeGroups[key][0];

                var index = newRenderer.sharedMesh.GetBlendShapeIndex(blendData.name);
                newRenderer.SetBlendShapeWeight(index, blendData.currentWeight);
            }

            newRenderer.transform.parent = parent;

            Debug.Log("Dynamic skinning merge complete with bone remapping!");
            #endregion


            #region Saving Final GameObject

#if UNITY_EDITOR
            BMAC_SaveSystem.LoadBodyMods(outfitSystem, outfitSystem.data);
            if (saveAsPrefab)
            {
                var settings = CharacterToolSettingsProvider.Get();

                var path = settings.prefabFolder;


                var saveName = sourceSystem.prefabName;
                Debug.Log(path);
                if (string.IsNullOrEmpty(saveName)) saveName = "NewCharacter";
                var assetPath = $"{path}/{saveName}";
                var savePath = $"Assets/{path}/{saveName}";

                if (!System.IO.Directory.Exists($"{Application.dataPath}{path}/{saveName}"))
                {
                    System.IO.Directory.CreateDirectory(assetPath);
                    AssetDatabase.Refresh();
                }

                //Saving Mesh
                string meshPath = $"{assetPath}/{saveName}_Mesh.asset";
                AssetDatabase.CreateAsset(newRenderer.sharedMesh, meshPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

                //Saving Materials and Textures
                var matList = new List<Material>();

                foreach (var item in newRenderer.sharedMaterials)
                {
                    var mat = new Material(item);
                    mat.name = saveName + "_" + item.name;

                    var rt = (RenderTexture)mat.mainTexture;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    RenderTexture.active = rt;

                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();

                    string diffusePath = $"{assetPath}/{mat.name}_D.png";
                    byte[] bytes = ImageConversion.EncodeToPNG(tex);
                    System.IO.File.WriteAllBytes(diffusePath, bytes);

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    var diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>($"{assetPath}/{mat.name}_D.png");

                    mat.mainTexture = diffuse;

                    UnityEngine.Object.Destroy(tex);

                    foreach (var matData in sourceSystem.materialData)
                    {
                        rt = (RenderTexture)item.GetTexture(matData.toMateiralProperty);
                        tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                        RenderTexture.active = rt;

                        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                        tex.Apply();

                        string texturePath = $"{assetPath}/{mat.name}{matData.toMateiralProperty}.png";
                        bytes = ImageConversion.EncodeToPNG(tex);
                        System.IO.File.WriteAllBytes(texturePath, bytes);

                        AssetDatabase.SaveAssets();
                        AssetDatabase.Refresh();
                        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{assetPath}/{mat.name}{matData.toMateiralProperty}.png");

                        mat.SetTexture(matData.toMateiralProperty, texture);

                        UnityEngine.Object.Destroy(tex);
                    }


                    //Saving Material
                    string matPath = $"{assetPath}/{mat.name}_Mat.mat";
                    AssetDatabase.CreateAsset(mat, matPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    matList.Add(AssetDatabase.LoadAssetAtPath<Material>(matPath));
                }

                newRenderer.sharedMaterials = matList.ToArray();

                await Task.Yield();

                // Save the prefab
                string prefabPath = $"{assetPath}/{saveName}.prefab";
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(parent.gameObject, prefabPath);
                var prefabSkinnedMeshrenderer = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
                prefabSkinnedMeshrenderer.sharedMesh = mesh;
                //prefabSkinnedMeshrenderer.sharedMaterial = mat;


                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"Saved Prefab at: {prefabPath}");
            }
#endif
            #endregion


            #endregion


            return parent.gameObject;


        }

        public List<Material> PackTextures(OutfitSystem system)
        {
            var mats = BoZo_TextureBuilder.GetMaterials(system);

            return mats;
        }

        public async Task<(Texture2D texture, Dictionary<string, Texture2D> additionalMaps, Dictionary<string, Rect> rect, Dictionary<string, Texture2D> customMaps)> CreateMergedTextures(List<Outfit> outfits)
        {
            var diffuseMaps = new List<Texture2D>();
            var normalMaps = new List<Texture2D>();
            var additionalMaps = new Dictionary<string, Texture2D[]>();
            var customMaps = new Dictionary<string, Texture2D[]>();
            var bakeMaterial = new Material(Shader.Find("BoZo/BakeTexture"));
            int index = 0;

            foreach (var item in outfits)
            {

                var smr = item.GetComponentInChildren<SkinnedMeshRenderer>();
                var renderer = item.GetComponentInChildren<Renderer>();
                var mesh = new List<Mesh>();

                foreach (var s in item.skinnedRenderers)
                {
                    mesh.Add(s.sharedMesh);
                }

                //Doesnt have a SkinnedMesh
                if (smr == null)
                {
                    mesh.Add(item.GetComponentInChildren<MeshFilter>(true).sharedMesh);
                }

                var originalMaterial = renderer.sharedMaterial;
                renderer.sharedMaterial = bakeMaterial;

                if (!item.customShader)
                {
                    //Copy All Colors and Data To Bake Material
                    bakeMaterial.mainTexture = originalMaterial.mainTexture;
                    bakeMaterial.SetTexture("_DecalMap", originalMaterial.GetTexture("_DecalMap"));
                    bakeMaterial.SetFloat("_DecalUVSet", originalMaterial.GetFloat("_DecalUVSet"));
                    bakeMaterial.SetFloat("_DecalBlend", originalMaterial.GetFloat("_DecalBlend"));
                    bakeMaterial.SetVector("_DecalScale", originalMaterial.GetVector("_DecalScale"));

                    bakeMaterial.SetTexture("_PatternMap", originalMaterial.GetTexture("_PatternMap"));
                    bakeMaterial.SetFloat("_PatternUVSet", originalMaterial.GetFloat("_PatternUVSet"));
                    bakeMaterial.SetFloat("_PatternBlend", originalMaterial.GetFloat("_PatternBlend"));
                    bakeMaterial.SetVector("_PatternScale", originalMaterial.GetVector("_PatternScale"));

                    for (int i = 0; i < 9; i++)
                    {
                        bakeMaterial.SetColor("_Color_" + (i + 1), originalMaterial.GetColor("_Color_" + (i + 1)));
                        bakeMaterial.SetColor("_Color_" + (i + 1), originalMaterial.GetColor("_Color_" + (i + 1)));

                        if (i + 1 <= 3)
                        {
                            bakeMaterial.SetColor("_DecalColor_" + (i + 1), originalMaterial.GetColor("_DecalColor_" + (i + 1)));
                            bakeMaterial.SetColor("_PatternColor_" + (i + 1), originalMaterial.GetColor("_PatternColor_" + (i + 1)));
                        }

                    }
                }
                else
                {
                    diffuseMaps.Add((Texture2D)originalMaterial.mainTexture);
                }

                foreach (var map in mergedMaterialDatas)
                {
                    if (!additionalMaps.ContainsKey(map.toMateiralProperty)) { additionalMaps[map.toMateiralProperty] = new Texture2D[outfits.Count]; }
                    additionalMaps[map.toMateiralProperty][index] = (Texture2D)originalMaterial.GetTexture(map.fromMateiralProperty);
                }

                //Get CustomMaps form extensions
                var extensions = item.GetComponentsInChildren<IOutfitExtension>();

                foreach (var extension in extensions)
                {
                    if (extension.GetValue() is Texture2D && !customMaps.ContainsKey(extension.GetID())) customMaps[extension.GetID()] = new Texture2D[outfits.Count];
                    if (extension.GetValue() is Texture2D)
                    {
                        customMaps[extension.GetID()][index] = (Texture2D)extension.GetValue();
                    }
                }


                var tex = await BakeTextureAsyncTask(mesh, bakeMaterial);

                diffuseMaps.Add(tex);

                renderer.sharedMaterial = originalMaterial;
                RenderTexture.active = null;

                index++;
            }


            int atlasSize = 2048;

            Texture2D atlas = new Texture2D(atlasSize, atlasSize);
            Texture2D atlasNormal = new Texture2D(atlasSize, atlasSize);
            Dictionary<string, Texture2D> additionalMapsList = new Dictionary<string, Texture2D>();
            Dictionary<string, Texture2D> atlasCustomMapsList = new Dictionary<string, Texture2D>();
            atlas.wrapMode = TextureWrapMode.Repeat;
            Rect[] rects = atlas.PackTextures(diffuseMaps.ToArray(), 0, atlasSize);

            //Creating Map for Outfits with Multiple Meshes
            Dictionary<string, Rect> rectMap = new Dictionary<string, Rect>();
            for (int i = 0; i < outfits.Count; i++)
            {
                rectMap.Add(outfits[i].name, rects[i]);
            }

            var pixels = atlas.GetPixels32();
            pixels = await DilateTextureAsync(atlas, pixels);

            atlas.SetPixels32(pixels);
            atlas.Apply();

            var addIndex = 0;
            foreach (var additional in additionalMaps)
            {
                var additionalValues = additional.Value.ToList();
                Texture2D newAdditionalMap = new Texture2D(atlasSize, atlasSize);
                newAdditionalMap.wrapMode = TextureWrapMode.Repeat;
                additionalMapsList[additional.Key] = await RemapTextureAsync(additionalValues, rects, atlasSize, newAdditionalMap, mergedMaterialDatas[addIndex].backgroundColor);
                addIndex++;
            }

            foreach (var custom in customMaps)
            {
                var customValues = custom.Value.ToList();
                if (customValues.Count == 0) continue;
                Texture2D newCustomMap = new Texture2D(atlasSize, atlasSize);
                newCustomMap.wrapMode = TextureWrapMode.Repeat;
                atlasCustomMapsList[custom.Key] = await RemapTextureAsync(customValues, rects, atlasSize, newCustomMap, Color.black);
            }

            return (atlas, additionalMapsList, rectMap, atlasCustomMapsList);
        }

        public async Task<Texture2D> BakeTextureAsyncTask(List<Mesh> mesh, Material bakeMaterial)
        {
            var textureSize = bakeMaterial.mainTexture.width;
            var rt = new RenderTexture(textureSize, textureSize, 0, RenderTextureFormat.ARGB32)
            {
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();

            CommandBuffer cb = new CommandBuffer();
            cb.SetRenderTarget(rt);
            cb.ClearRenderTarget(true, true, Color.clear);
            foreach (var m in mesh)
            {
                cb.DrawMesh(m, Matrix4x4.identity, bakeMaterial);
            }
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();

            var tcs = new TaskCompletionSource<Texture2D>();

            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError)
                {
                    tcs.SetException(new Exception("Async GPU readback failed."));
                }
                else
                {
                    var data = request.GetData<Color32>();
                    var tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
                    tex.LoadRawTextureData(data);
                    tex.Apply();
                    tcs.SetResult(tex);
                }
            });

            return await tcs.Task;
        }

        public async Task<Texture2D> RemapTextureAsync(List<Texture2D> normalMaps, Rect[] rects, int atlasSize, Texture2D atlasNormal, Color fillColor)
        {
            List<Task> pendingReads = new List<Task>();

            Color[] pixels = new Color[atlasSize * atlasSize];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fillColor;
            }
            atlasNormal.SetPixels(pixels);

            for (int i = 0; i < normalMaps.Count; i++)
            {
                if (normalMaps[i] == null) continue;
                Rect r = rects[i];

                int x = Mathf.RoundToInt(r.x * atlasSize);
                int y = Mathf.RoundToInt(r.y * atlasSize);
                int w = Mathf.RoundToInt(r.width * atlasSize);
                int h = Mathf.RoundToInt(r.height * atlasSize);

                Texture2D normal = normalMaps[i];

                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(normal, rt);

                int copyX = x, copyY = y, copyW = w, copyH = h;
                RenderTexture copyRT = rt;

                var tcs = new TaskCompletionSource<bool>();
                pendingReads.Add(tcs.Task);

                AsyncGPUReadback.Request(copyRT, 0, TextureFormat.RGBA32, request =>
                {
                    if (request.hasError)
                    {
                        Debug.LogError("Normal map readback failed");
                        tcs.SetResult(true);
                        RenderTexture.ReleaseTemporary(copyRT);
                        return;
                    }

                    var data = request.GetData<Color32>();
                    Color32[] pixels = data.ToArray();

                    // Fill into the atlas
                    Color[] colors = new Color[pixels.Length];
                    for (int j = 0; j < pixels.Length; j++)
                    {
                        colors[j] = pixels[j]; // Converts Color32 to Color
                    }

                    atlasNormal.SetPixels(copyX, copyY, copyW, copyH, colors);

                    RenderTexture.ReleaseTemporary(copyRT);
                    tcs.SetResult(true);
                });
            }

            await Task.WhenAll(pendingReads);

            atlasNormal.Apply(); // Apply once after all blocks are set
            return atlasNormal;
        }



        public async Task<Color32[]> DilateTextureAsync(Texture2D tex, Color32[] pixels)
        {
            var iterations = 2;
            int w = tex.width;
            int h = tex.height;

            Color32[] resultPixels = await Task.Run(() =>
            {
                Color32[] workingPixels = new Color32[pixels.Length];
                Color32[] temp = new Color32[pixels.Length];
                bool[] originalMask = new bool[pixels.Length];

                // Initialize workingPixels and mask
                for (int i = 0; i < pixels.Length; i++)
                {
                    workingPixels[i] = pixels[i];
                    originalMask[i] = pixels[i].a >= 255; // original valid pixels
                }

                for (int it = 0; it < iterations; it++)
                {
                    workingPixels.CopyTo(temp, 0);

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int idx = y * w + x;
                            if (workingPixels[idx].a >= 255) continue;

                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dy == 0) continue;

                                    int nx = x + dx;
                                    int ny = y + dy;
                                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;

                                    int nIdx = ny * w + nx;

                                    // ONLY copy from original source pixels
                                    if (originalMask[nIdx])
                                    {
                                        temp[idx] = workingPixels[nIdx];
                                        goto FoundPixel;
                                    }
                                }
                            }

                        FoundPixel:;
                        }
                    }

                    // Update working pixels
                    var swap = workingPixels;
                    workingPixels = temp;
                    temp = swap;

                    // After first iteration, also allow pixels from the new ones as source
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (workingPixels[i].a >= 1)
                            originalMask[i] = true;
                    }
                }

                return workingPixels;
            });

            return resultPixels;
        }



        private class boneData
        {
            public Transform bone;
            public int index;
        }

        struct BlendshapeData
        {
            public string name;
            public float weight;
            public float currentWeight;
            public Vector3[] deltaVertices;
            public Vector3[] deltaNormals;
            public Vector3[] deltaTangents;
            public int vertexOffset;
        }

    }

    [System.Serializable]
    public class MergedMaterialData
    {
        public string fromMateiralProperty;
        public string toMateiralProperty;
        public Color backgroundColor = Color.gray;
    }

}
