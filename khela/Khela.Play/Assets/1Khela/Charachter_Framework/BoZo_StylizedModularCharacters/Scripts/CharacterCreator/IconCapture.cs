#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Bozo.ModularCharacters
{
    public class IconCapture : MonoBehaviour
    {
        [SerializeField] OutfitSystem outfitSystem;
        [SerializeField] RenderTexture iconTexture;
        [SerializeField] Camera iconCamera;
        [SerializeField] Transform parent;
        [SerializeField] string path = "BoZo_ModularAnimeCharacters/Textures/OutfitIcons";

        [SerializeField] GameObject BackingBody;
        [SerializeField] GameObject BackingHead;
        [SerializeField] List<Outfit> outfits = new List<Outfit>();
        [SerializeField] List<Outfit> outfitsQueue = new List<Outfit>();
        private Outfit activeOutfit;
        [SerializeField] Color[] Colors;

        private Camera activeCam;
        [SerializeField] IconCaptureSettings[] cameraSettings;
        [SerializeField] Dictionary<OutfitType, IconCaptureSettings> cameras = new Dictionary<OutfitType, IconCaptureSettings>();

        private void Awake()
        {
            foreach (var item in cameraSettings)
            {
                cameras.Add(item.type, item);
            }
        }

        [ContextMenu("Capture")]
        public void Capture()
        {
#if UNITY_EDITOR
            outfitsQueue = new List<Outfit>(outfits);
            PrepareCapture();
#endif
        }

        public void Capture(GameObject gameObject)
        {
#if UNITY_EDITOR
            outfits.Clear();
            var outfit = gameObject.GetComponent<Outfit>();
            if (outfit == null) return;

            path = AssetDatabase.GetAssetPath(gameObject.gameObject);
            path = path.Replace(gameObject.name + ".prefab", "");
            path = path.Replace("Assets", "");

            outfits.Add(outfit);

            CaptureCoroutine();
#endif
        }
        private void PrepareCapture()
        {
            if (outfitsQueue.Count == 0) return; 
            outfitSystem.OnRigChanged += CaptureIcon;
            activeOutfit = outfitsQueue[0];
            outfitsQueue.RemoveAt(0);
            var outfit = Instantiate(activeOutfit, parent);
            for (int i = 0; i < Colors.Length; i++)
            {
                outfit.SetColor(Colors[i], i + 1);
            }
        }

        private async void CaptureIcon(SkinnedMeshRenderer smr)
        {
            if (activeCam != null) activeCam.gameObject.SetActive(false);
            activeCam = cameras[activeOutfit.Type].camera;
            activeCam.gameObject.SetActive(true);

            BackingBody.SetActive(cameras[activeOutfit.Type].showBody);
            BackingHead.SetActive(cameras[activeOutfit.Type].showHead);

            activeCam.Render();
            await Task.Yield();

            RenderTexture.active = iconTexture;

            Texture2D icon = new Texture2D(iconTexture.width, iconTexture.height, TextureFormat.RGBA32, false);
            Rect rect = new Rect(new Rect(0, 0, iconTexture.width, iconTexture.height));
            icon.ReadPixels(rect, 0, 0);
            icon.Apply();

            byte[] bytes = icon.EncodeToPNG();

            System.IO.File.WriteAllBytes(Application.dataPath + "/" + path + "/" + activeOutfit.name + ".png", bytes);
            AssetDatabase.Refresh();
            print("IconCreated: " + Application.dataPath + "/" + path + "/" + activeOutfit.name + ".png");

            TextureImporter importer = AssetImporter.GetAtPath("Assets/" + path + "/" + activeOutfit.name + ".png") as TextureImporter;

            outfitSystem.RemoveOutfit(activeOutfit.Type);

            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.SaveAndReimport();
            }

            if (activeOutfit)
            {
                var spritrIcon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/" + path + "/" + activeOutfit.name + ".png");
                activeOutfit.OutfitIcon = spritrIcon;
                EditorUtility.SetDirty(activeOutfit);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            if (activeCam != null) activeCam.gameObject.SetActive(false);

            outfitSystem.OnRigChanged -= CaptureIcon;
            PrepareCapture();
        }

#if UNITY_EDITOR
        private async void CaptureCoroutine()
        {

            foreach (var item in outfits)
            {
                //Match the camera to the outfit
                if (activeCam != null) activeCam.gameObject.SetActive(false);
                activeCam = cameras[item.Type].camera;
                activeCam.gameObject.SetActive(true);

                BackingBody.SetActive(cameras[item.Type].showBody);
                BackingHead.SetActive(cameras[item.Type].showHead);


                var outfit = Instantiate(item, parent);
                if (!outfit.customShader)
                {
                    for (int i = 0; i < Colors.Length; i++)
                    {
                        outfit.SetColor(Colors[i], i + 1);
                    }
                }

                activeCam.Render();
                await Task.Yield();

                RenderTexture.active = iconTexture;

                Texture2D icon = new Texture2D(iconTexture.width, iconTexture.height, TextureFormat.RGBA32, false);
                Rect rect = new Rect(new Rect(0, 0, iconTexture.width, iconTexture.height));
                icon.ReadPixels(rect, 0, 0);
                icon.Apply();

                byte[] bytes = icon.EncodeToPNG();

                System.IO.File.WriteAllBytes(Application.dataPath + "/" + path + "/" + item.name + ".png", bytes);
                AssetDatabase.Refresh();
                print("IconCreated: " + Application.dataPath + "/" + path + "/" + item.name + ".png");

                TextureImporter importer = AssetImporter.GetAtPath("Assets/" + path + "/" + item.name + ".png") as TextureImporter;

                outfitSystem.RemoveOutfit(outfit.Type);

                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.SaveAndReimport();
                }

                if (outfit)
                {
                    var spritrIcon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/" + path + "/" + item.name + ".png");
                    item.OutfitIcon = spritrIcon;
                    EditorUtility.SetDirty(item);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
                if (activeCam != null) activeCam.gameObject.SetActive(false);
            }
        }
#endif


        [System.Serializable]
        public class IconCaptureSettings
        {
            public OutfitType type;
            public Camera camera;
            public bool showHead = true;
            public bool showBody = true;
        }
    }

}
#endif
