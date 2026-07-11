using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar.EditorTools
{
    /// <summary>
    /// One-click builder for the 4 WardrobeController tile prefabs (SliderTile / SlotTab / PartTile / SwatchTile),
    /// correctly structured (Slider min0-max1, "Icon"/"Selected" children, TMP labels). Menu: Khela ▸ Avatar ▸ Create
    /// Wardrobe Tiles. Writes to Assets/1Khela/Prefabs/Wardrobe. Assign the results to the WardrobeController.
    /// </summary>
    public static class WardrobeTileFactory
    {
        private const string Folder = "Assets/1Khela/Prefabs/Wardrobe";

        private static readonly Color Bg = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color Sel = new Color(0.20f, 0.65f, 1f, 0.55f);

        [MenuItem("Khela/Avatar/Create Wardrobe Tiles")]
        public static void CreateTiles()
        {
            EnsureFolder();
            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

            BuildSliderTile();
            BuildSlotTab(sprite);
            BuildPartTile(sprite);
            BuildSwatchTile(sprite);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var made = AssetDatabase.LoadAssetAtPath<GameObject>($"{Folder}/PartTile.prefab");
            if (made != null) { EditorGUIUtility.PingObject(made); Selection.activeObject = made; }
            Debug.Log($"[WardrobeTileFactory] Built SliderTile / SlotTab / PartTile / SwatchTile in {Folder}. Assign them on the WardrobeController.");
        }

        // ---- tiles ----

        private static void BuildSliderTile()
        {
            var res = new DefaultControls.Resources
            {
                standard   = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
                background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
                knob       = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            };
            var go = DefaultControls.CreateSlider(res);
            go.name = "SliderTile";
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(400f, 60f);

            var slider = go.GetComponent<Slider>();
            slider.minValue = 0f; slider.maxValue = 1f; slider.wholeNumbers = false; slider.value = 0.5f;

            // Push the slider graphics to the bottom half, put the Label across the top.
            foreach (RectTransform child in rt)
            {
                child.anchorMin = new Vector2(child.anchorMin.x, 0f);
                child.anchorMax = new Vector2(child.anchorMax.x, 0.5f);
            }
            var label = AddTMP(rt, "Label", "Shape", 22);
            label.alignment = TextAlignmentOptions.Left;
            var lr = label.rectTransform;
            lr.anchorMin = new Vector2(0f, 0.5f); lr.anchorMax = new Vector2(1f, 1f);
            lr.offsetMin = new Vector2(6f, 0f); lr.offsetMax = new Vector2(-6f, 0f);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 60f; le.minHeight = 60f;

            Save(go, "SliderTile");
        }

        private static void BuildSlotTab(Sprite sprite)
        {
            var rt = NewButton("SlotTab", sprite, Bg, out _);
            rt.sizeDelta = new Vector2(140f, 56f);
            var label = AddTMP(rt, "Label", "Tab", 24);
            Stretch(label.rectTransform, 4f);
            Add<LayoutElement>(rt.gameObject, le => { le.preferredWidth = 140f; le.preferredHeight = 56f; });
            Save(rt.gameObject, "SlotTab");
        }

        private static void BuildPartTile(Sprite sprite)
        {
            var rt = NewButton("PartTile", sprite, Bg, out _);
            rt.sizeDelta = new Vector2(150f, 180f);

            var icon = AddImage(rt, "Icon", sprite: null, Color.white);   // controller sets .sprite
            Stretch(icon.rectTransform, 8f, topFraction: 0.28f);           // fills the top ~72%

            var label = AddTMP(rt, "Label", "", 20);
            var lr = label.rectTransform;
            lr.anchorMin = Vector2.zero; lr.anchorMax = new Vector2(1f, 0.28f);
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;

            var sel = AddImage(rt, "Selected", sprite, Sel);
            Stretch(sel.rectTransform, 0f);
            sel.gameObject.SetActive(false);   // toggled on for the equipped part

            Save(rt.gameObject, "PartTile");
        }

        private static void BuildSwatchTile(Sprite sprite)
        {
            var rt = NewButton("SwatchTile", sprite, Color.white, out _);   // white border
            rt.sizeDelta = new Vector2(64f, 64f);

            var icon = AddImage(rt, "Icon", sprite: null, Color.white);      // controller sets .color = swatch
            Stretch(icon.rectTransform, 4f);

            var sel = AddImage(rt, "Selected", sprite, Sel);
            Stretch(sel.rectTransform, 0f);
            sel.gameObject.SetActive(false);   // REQUIRED: swatch highlight is the ring, not a tint

            Save(rt.gameObject, "SwatchTile");
        }

        // ---- builders ----

        private static RectTransform NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static RectTransform NewButton(string name, Sprite sprite, Color color, out Button button)
        {
            var rt = NewUI(name, null);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite; img.type = Image.Type.Sliced; img.color = color;
            button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            return rt;
        }

        private static Image AddImage(Transform parent, string name, Sprite sprite, Color color)
        {
            var rt = NewUI(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite; img.color = color;
            if (sprite != null) img.type = Image.Type.Sliced;
            return img;
        }

        private static TextMeshProUGUI AddTMP(Transform parent, string name, string text, float size)
        {
            var rt = NewUI(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.font = TMP_Settings.defaultFontAsset;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private static void Stretch(RectTransform rt, float pad, float topFraction = 0f)
        {
            rt.anchorMin = new Vector2(0f, topFraction);
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        private static void Add<T>(GameObject go, System.Action<T> configure) where T : Component
        {
            var c = go.AddComponent<T>();
            configure?.Invoke(c);
        }

        private static void Save(GameObject go, string name)
        {
            PrefabUtility.SaveAsPrefabAsset(go, $"{Folder}/{name}.prefab");
            Object.DestroyImmediate(go);
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/1Khela/Prefabs"))
                AssetDatabase.CreateFolder("Assets/1Khela", "Prefabs");
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/1Khela/Prefabs", "Wardrobe");
        }
    }
}
