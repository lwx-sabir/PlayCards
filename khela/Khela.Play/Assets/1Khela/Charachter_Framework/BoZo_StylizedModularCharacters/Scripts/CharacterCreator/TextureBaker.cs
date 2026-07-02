using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bozo.ModularCharacters
{


    public class TextureBaker : MonoBehaviour
    {
#if UNITY_EDITOR
        public CharacterCreator characterCreator;
        public Material BakeMaterial;
        public Material UserMaterial;
        public IconCapture IconCapture;

        [Header("ExportSettings")]
        [SerializeField] string outfitName;
        [SerializeField] OutfitType OutfitToExport;
        [SerializeField] int textureDilation = 20;

        private string savePath = Application.dataPath + "/BoZo_StylizedModularCharacters/CustomOutfits/Resources";
        private string AssetPath = "Assets/BoZo_StylizedModularCharacters/CustomOutfits/Resources";

        [ContextMenu("ExportOutfit")]
        public void ExportOutfit()
        {

            if (!Application.isPlaying)
            {
                Debug.LogWarning("Please Enter Play Mode to export an Outfit");
                return;
            }

            if (outfitName.Length == 0)
            {
                Debug.LogWarning("Please enter name for export");
                return;
            }

            if (UserMaterial == null)
            {
                Debug.LogWarning("Material Empty please select a source material");
                return;
            }

            if (OutfitToExport == null)
            {
                Debug.LogWarning("Empty OutfitType Please Provide an Outfit Type");
                return;
            }

            var character = characterCreator.character;
            var sourceOutfit = character.GetOutfit(OutfitToExport);
            if (sourceOutfit == null)
            {
                Debug.LogWarning("Outfit is Null make sure outfit exists on character");
                return;
            }
            var cleanName = sourceOutfit.name;
            cleanName = cleanName.Replace("(Clone)", "");
            var NewOutfit = Instantiate(characterCreator.GetOutfit(cleanName));


            BakeOutfit(sourceOutfit, NewOutfit, outfitName);

        }

        public void CreateOutfit()
        {
            var character = characterCreator.character;
            var sourceOutfit = character.GetOutfit(OutfitToExport);
            if (sourceOutfit == null)
            {
                Debug.LogWarning("Outfit is Null make sure outfit exists on character");
                return;
            }
            var cleanName = sourceOutfit.name;
            cleanName = cleanName.Replace("(Clone)", "");
            var NewOutfit = Instantiate(characterCreator.GetOutfit(cleanName));


            BakeOutfit(sourceOutfit, NewOutfit, outfitName);
        }

        public void BakeOutfit(Outfit sourceOutfit, Outfit newOutfit, string outfitName = "NewOutfit")
        {

            Renderer renderer = newOutfit.GetComponentInChildren<Renderer>();
            var tex = CreateTexture(sourceOutfit, newOutfit);

            //Create Save Path
            string folderPath = savePath + "/" + sourceOutfit.Type.name;

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                System.IO.Directory.CreateDirectory(folderPath);
                Debug.Log("Created folder: " + folderPath);
                AssetDatabase.Refresh();
            }

            // Save PNG
            byte[] bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes(folderPath + "/" + sourceOutfit.Type.name + "_" + outfitName + "_Swatch_1.png", bytes);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            //Save Asset

            string path = AssetPath + "/" + sourceOutfit.Type.name + "/" + sourceOutfit.Type.name + "_" + outfitName;

            Material newMaterial = new Material(UserMaterial);
            var newTex = AssetDatabase.LoadAssetAtPath<Texture2D>(path + "_Swatch_1.png");
            newMaterial.mainTexture = newTex;

            AssetDatabase.CreateAsset(newMaterial, path + "_mat.mat");
            var newMat = AssetDatabase.LoadAssetAtPath<Material>(path + "_mat.mat");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            newMat.mainTexture = newTex;
            renderer.material = newMat;

            AssetDatabase.DeleteAsset(path + ".prefab");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var swatch = new OutfitSwatch();
            swatch.swatchID = folderPath + "/" + folderPath + "_" + outfitName + "_Swatch_" + 1;
            newOutfit.outfitSwatches.Add(swatch);
            newOutfit.customShader = true;

            var savedPrefab = PrefabUtility.SaveAsPrefabAsset(newOutfit.gameObject, path + ".prefab");
            EditorUtility.SetDirty(savedPrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Destroy(newOutfit.gameObject);
            IconCapture.Capture(savedPrefab);
            Debug.Log("Swatched Created: " + path);

        }

        [ContextMenu("CreateSwatch")]
        public void CreateSwatch()
        {


            if (!Application.isPlaying)
            {
                Debug.LogWarning("Please Enter Play Mode to create Swatch");
                return;
            }

            if (outfitName.Length == 0)
            {
                Debug.LogWarning("Please enter name for export");
                return;
            }

            var character = characterCreator.character;
            var sourceOutfit = character.GetOutfit(OutfitToExport);
            var cleanName = sourceOutfit.name;
            cleanName = cleanName.Replace("(Clone)", "");
            var newOutfit = Instantiate(characterCreator.GetOutfit(cleanName));

            string folderPath = sourceOutfit.Type.name;
            string path = AssetPath + "/" + folderPath + "/" + folderPath + "_" + outfitName + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            print(path);

            if (prefab == null)
            {
                Debug.LogWarning("Custom Outfit does not exist. Please Use Export before creating swatches");
                Destroy(newOutfit.gameObject);
                return;
            }

            int count = 1;
            string SwatchPath = savePath + "/" + folderPath + "/" + folderPath + "_" + outfitName + "_Swatch_" + count + ".png";

            do
            {
                count++;
                SwatchPath = savePath + "/" + folderPath + "/" + folderPath + "_" + outfitName + "_Swatch_" + count + ".png";
                print(count);
            } while (System.IO.File.Exists(SwatchPath));


            var tex = CreateTexture(sourceOutfit, newOutfit);
            byte[] bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes(SwatchPath, bytes);

            var outfit = prefab.GetComponent<Outfit>();
            var swatch = new OutfitSwatch();
            swatch.swatchID = folderPath + "/" + folderPath + "_" + outfitName + "_Swatch_" + count;
            outfit.outfitSwatches.Add(swatch);


            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Destroy(newOutfit.gameObject);
            Debug.Log("Swatched Created: " + path);

        }

        Texture2D CreateTexture(Outfit sourceOutfit, Outfit newOutfit)
        {
            Mesh mesh = null;
            Renderer renderer = newOutfit.GetComponentInChildren<Renderer>();
            Material sourceMaterial = sourceOutfit.GetComponentInChildren<Renderer>().material;

            if (renderer.TryGetComponent<MeshRenderer>(out MeshRenderer meshRenderer))
            {
                mesh = meshRenderer.GetComponent<MeshFilter>().sharedMesh;
            }
            else if (renderer.TryGetComponent<SkinnedMeshRenderer>(out SkinnedMeshRenderer skinnedMeshRenderer))
            {
                mesh = skinnedMeshRenderer.sharedMesh;
            }
            else
            {
                return null;
            }

            renderer.material = BakeMaterial;

            Material material = renderer.material;

            //Copy All Colors and Data To Bake Material
            material.mainTexture = sourceMaterial.mainTexture;
            material.SetTexture("_DecalMap", sourceMaterial.GetTexture("_DecalMap"));
            material.SetFloat("_DecalUVSet", sourceMaterial.GetFloat("_DecalUVSet"));
            material.SetFloat("_DecalBlend", sourceMaterial.GetFloat("_DecalBlend"));
            material.SetVector("_DecalScale", sourceMaterial.GetVector("_DecalScale"));

            material.SetTexture("_PatternMap", sourceMaterial.GetTexture("_PatternMap"));
            material.SetFloat("_PatternUVSet", sourceMaterial.GetFloat("_PatternUVSet"));
            material.SetFloat("_PatternBlend", sourceMaterial.GetFloat("_PatternBlend"));
            material.SetVector("_PatternScale", sourceMaterial.GetVector("_PatternScale"));


            for (int i = 0; i < 8; i++)
            {
                material.SetColor("_Color_" + (i + 1), sourceMaterial.GetColor("_Color_" + (i + 1)));
                material.SetColor("_Color_" + (i + 1), sourceMaterial.GetColor("_Color_" + (i + 1)));

                if (i + 1 <= 3)
                {
                    material.SetColor("_DecalColor_" + (i + 1), sourceMaterial.GetColor("_DecalColor_" + (i + 1)));
                    material.SetColor("_PatternColor_" + (i + 1), sourceMaterial.GetColor("_PatternColor_" + (i + 1)));
                }

            }

            var textureSize = 2048;
            // Create RenderTexture
            RenderTexture rt = new RenderTexture(textureSize, textureSize, 0);
            rt.wrapMode = TextureWrapMode.Clamp;

            // Create command buffer to render the mesh
            CommandBuffer cb = new CommandBuffer();
            cb.SetRenderTarget(rt);
            cb.ClearRenderTarget(true, true, Color.clear);

            Matrix4x4 identity = Matrix4x4.identity;
            cb.DrawMesh(mesh, identity, material);

            // Execute
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();

            // Read pixels
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, textureSize, textureSize), 0, 0);
            tex = DilateTexture(tex);
            tex.Apply();

            RenderTexture.active = null;
            return tex;

        }

        Texture2D DilateTexture(Texture2D tex)
        {
            var iterations = textureDilation;
            int w = tex.width;
            int h = tex.height;

            Color32[] pixels = tex.GetPixels32();
            Color32[] temp = new Color32[pixels.Length];

            for (int it = 0; it < iterations; it++)
            {
                pixels.CopyTo(temp, 0);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (pixels[idx].a != 0) continue;

                        // Look around
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;

                                int nIdx = ny * w + nx;
                                if (pixels[nIdx].a != 0)
                                {
                                    temp[idx] = pixels[nIdx];
                                    goto NextPixel;
                                }
                            }
                        }

                    NextPixel:;
                    }
                }

                // Swap buffers
                Color32[] swap = pixels;
                pixels = temp;
                temp = swap;
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;

        }
#endif
    }
}
