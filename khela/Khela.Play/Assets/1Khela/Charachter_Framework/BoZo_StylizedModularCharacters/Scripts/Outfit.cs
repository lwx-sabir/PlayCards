using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;


namespace Bozo.ModularCharacters
{
    public class Outfit : OutfitBase
    {
        public bool Initalized { get; set; }
        [SerializeField] bool AttachInEditMode;

        private OutfitSystem system;

        //[Header("Character Creator Settings")]
        public string OutfitName;
        public Sprite OutfitIcon;
        public string[] ColorChannels = new string[] { "Base" };
        public string TextureCatagory;

        [Tooltip("Merges Texture into a atlas. Turn this off if your texture doesn't align with the atlas or you want a unique shader")]
        public bool mergeTexture = true;
        [Tooltip("Merges Mesh. Turn this off if you want this to be a seperate mesh")]
        public bool mergeMesh = true;

        public bool supportDecals;
        public bool supportPatterns;
        public bool showCharacterCreator = true;


        public SkinnedMeshRenderer skinnedRenderer
        {
            get
            {
                if (skinnedRenderers.Length > 0) return skinnedRenderers[0];
                else return null;
            }
        }
        public SkinnedMeshRenderer[] skinnedRenderers { get; private set; }
        public Renderer outfitRenderer { get; private set; }

        //[field: Header("Outfit Settings")]
        [SerializeField] public OutfitType Type;
        public string AttachPoint;
        [Range(-1, 11)] public int materialIndex = -1;
        [Range(0, 100)] public int materialPriority = 0;


        //Colors
        public Color[] colors = new Color[9];
        public Color[] defaultColors;
        //Pattern Colors
        public Texture pattern;
        public Color[] patternColors = new Color[3];
        public Vector2 patternSize;
        //Decals Colors
        private DecalBaker decalBaker;
        public List<DecalData> decalDatas;

        public string[] tags;
        public string[] categories;

        public GameObject[] optionalPieces;
        public Transform[] additionalBones;

        private Dictionary<string, int> tagShapes = new Dictionary<string, int>();
        private Dictionary<string, int> shapes = new Dictionary<string, int>();
        public LinkedColorSets[] LinkedColorSets;
        public OutfitType[] IncompatibleSets;
        public OutfitType[] HideTypes;

        //[Header("User Settings")]
        public int currentSwatch;
        public List<OutfitSwatch> outfitSwatches = new List<OutfitSwatch>();

        public Transform[] originalBones;
        public Transform originalRootBone;
        public Transform editorAttachPoint;

        //Materials
        public bool customShader;
        public Material material;
        public Renderer controlMaterial;
        public int controlIndex;
        public Dictionary<string, Material> editMaterials = new Dictionary<string, Material>();
        private MaterialPropertyBlock block;


        //ExtraMaps
        public Texture2D MC2Map;



        //Events
        public UnityAction<Outfit> OnColorChanged;
        public UnityAction<Outfit> OnOutfitChanged;
        //
        public bool attached;


        private void OnValidate()
        {
            if (Application.isPlaying && gameObject.scene.isLoaded && gameObject.activeSelf)
            {

                if (system == null) system = GetComponentInParent<OutfitSystem>();
                SetColorInital();
            }
#if UNITY_EDITOR
            if (AttachInEditMode && !Application.isPlaying) SoftAttach();
#endif
        }

        private void Awake()
        {
            outfitRenderer = GetComponentInChildren<Renderer>();
            skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (outfitRenderer) material = outfitRenderer.sharedMaterial;
            block = new MaterialPropertyBlock();

            foreach (var smr in skinnedRenderers)
            {
                smr.sharedMaterial = material;
            }

            if (material)
            {
                for (int i = 1; i < 10; i++)
                {
                    colors[i - 1] = material.GetColor("_Color_" + i);
                }
            }

            InitSetUpShapes();
        }

        private void OnDestroy()
        {
            if (!system) return;
            system.OnOutfitChanged -= OnOutfitUpdate;
            system.OnShapeChanged -= SetShape;
        }

        private void Start()
        {
            if (!Initalized) Attach();
            SetColorInital();
        }

        public void Attach(Transform parent)
        {
            transform.parent = parent;
            Attach();
        }

        public void Attach(OutfitSystem system)
        {
            transform.parent = system.transform;
            Attach();
        }

        public void Attach(bool force = false)
        {
            if (attached) return;

            system = GetComponentInParent<OutfitSystem>();
            outfitRenderer = GetComponentInChildren<Renderer>();
            if (system == null) return;
            if (!system.initalized) return;

            InitDecals();

            CopySystemShapes();

            system.OnOutfitChanged += OnOutfitUpdate;
            system.OnShapeChanged += SetShape;

            var extensions = GetComponentsInChildren<IOutfitExtension>();
            foreach (var item in extensions) { item.Initalize(system, this); }


            //Reassigning original bones incase it was attached in editor
            if (originalBones.Length > 0 && skinnedRenderer)
            {
                skinnedRenderer.bones = originalBones;
                skinnedRenderer.rootBone = originalRootBone;
            }

            system.AttachOutfit(this);
            CheckTags();

            foreach (var item in extensions) { item.Execute(system, this); }

            attached = true;
        }

        public void ReturnBones()
        {
            foreach (var item in additionalBones)
            {
                if (item == null) continue;
                item.parent = transform;
            }
        }

        #region OnUpdate Methods

        private void OnOutfitUpdate(Outfit newOutfit)
        {
            CheckTags();
        }

        private void CheckTags()
        {
            var shapes = new List<string>(tagShapes.Keys);
            foreach (var smr in skinnedRenderers)
            {
                for (int i = 0; i < shapes.Count; i++)
                {
                    var yes = system.ContainsTag(shapes[i]);
                    if (yes) { smr.SetBlendShapeWeight(tagShapes[shapes[i]], 100); }
                    else { smr.SetBlendShapeWeight(tagShapes[shapes[i]], 0); }
                }
            }
        }

        #endregion

        #region Shapes Methods
        private void CopySystemShapes()
        {
            if (system)
            {
                var shapesKeys = shapes.Keys.ToArray();



                for (int i = 0; i < shapesKeys.Length; i++)
                {
                    var systemValue = system.GetShape(shapesKeys[i]);
                    if (systemValue == -10000) continue;

                    SetShape(shapesKeys[i], systemValue);
                }
            }
        }

        private void InitSetUpShapes()
        {
            if (!skinnedRenderer) return;
            var mesh = skinnedRenderer.sharedMesh;
            var blendShapeCount = skinnedRenderer.sharedMesh.blendShapeCount;

            for (int i = 0; i < blendShapeCount; i++)
            {
                var blendName = mesh.GetBlendShapeName(i);

                int dot = blendName.IndexOf('.');
                if (dot >= 0) blendName = blendName.Substring(dot + 1);

                int us = blendName.IndexOf('_');
                if (us < 0) continue;

                if (string.CompareOrdinal(blendName, 0, "Tag_", 0, 4) == 0)
                {
                    tagShapes.Add(blendName.Substring(4), i);
                }
                else if (string.CompareOrdinal(blendName, 0, "Shape_", 0, 6) == 0)
                {
                    shapes.Add(blendName.Substring(6), i);
                }
            }
        }

        public void SetShape(string key, float value)
        {
            if (value <= -10000) return;
            if (!skinnedRenderer) return;
            var sort = key.Split(".");
            if (sort.Length > 1) { key = sort[1]; }

            if (!shapes.TryGetValue(key, out int index)) { return; }


            foreach (var smr in skinnedRenderers)
            {

                if (smr.sharedMesh.blendShapeCount <= index) continue;
                smr.SetBlendShapeWeight(index, value);
            }
        }

        public void SetPartActive(int index, bool active)
        {
            if (index >= 0 || index < optionalPieces.Length)
            {
                optionalPieces[index].SetActive(active);
                OnOutfitChanged?.Invoke(this);
            }

        }

        #endregion

        public void SetControlMaterial(Renderer mat, int index)
        {
            //This is for controlling a proxy material so the outfit can use another shader entirely
            controlMaterial = mat;
            controlIndex = index;
        }

        public void SetMaterial(Material mat)
        {
            if (outfitRenderer) outfitRenderer.sharedMaterial = mat;
            foreach (var smr in skinnedRenderers)
            {
                smr.sharedMaterial = mat;
            }
        }

        #region colors

        public void InitDecals()
        {
            if (!supportDecals) return;
            gameObject.AddComponent<DecalBaker>();
            decalBaker = GetComponent<DecalBaker>();
        }


        private void SetColorInital()
        {
            if (!outfitRenderer) return;

            for (int i = 0; i < defaultColors.Length; i++)
            {
                SetColor(defaultColors[i], i + 1);
            }
        }

        public void SetColor(Color color, int channel, bool linkedChanged = false)
        {
            if (channel - 1 < 0 || channel - 1 > colors.Length) return;


            colors[channel - 1] = color;


            if (system == null) { system = GetComponentInParent<OutfitSystem>(); }
            if (outfitRenderer == null) { outfitRenderer = GetComponentInChildren<Renderer>(); }
            var material = SelectBlock();

            try
            {
                if (customShader)
                {
                    SetColor(color);
                }
                else
                {
                    material.SetColor("_Color_" + channel, color);
                }
            }
            catch
            {
                Debug.LogError(name + " Failed to load colors. Is it missing its material or renderer?");
            }


            foreach (var item in LinkedColorSets)
            {
                if (!linkedChanged)
                {
                    var linkedOutfit = system.GetOutfit(item.linkedType);
                    if (linkedOutfit == null) continue;
                    if (channel > item.linkedChannelRange) continue;
                    linkedOutfit.SetColor(color, channel, true);
                }
            }

            UpdateMaterialBlock();
            OnColorChanged?.Invoke(this);
        }

        public virtual void SetColor(Color color)
        {
            colors[0] = color;
            var material = SelectBlock();
            if (material.HasProperty("_Color_1"))
            {
                material.SetColor("_Color_1", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            else if (material.HasProperty("_MainColor"))
            {
                material.SetColor("_MainColor", color);
            }

            UpdateMaterialBlock();
        }

        public virtual List<Color> GetColors()
        {
            return colors.ToList();
        }

        public virtual Color GetColor(int channel)
        {
            if(channel - 1 < 0 || channel - 1 > colors.Length)
            {
                return colors[0];
            }
            else
            {
                return colors[channel - 1];
            }
        }

        public void ApplyDecals(RenderTexture rt, RenderTexture crt)
        {
            if (!decalBaker) return;
            foreach (var data in decalDatas)
            {
                decalBaker.BakeDecal(rt,crt,data);
            }
        }

        public void AddDecal()
        {
            var data = new DecalData("", "spine_04", new(0,0,-0.05f), 0, 0, 0, 0.15f, Color.white);
            decalDatas.Add(data);
            OnColorChanged?.Invoke(this);
        }

        public void RemoveDecal()
        {
            decalDatas.RemoveAt(decalDatas.Count - 1);
            OnColorChanged?.Invoke(this);
        }
        public virtual bool GetDecal(int index, out DecalData data)
        {
            if (index >= 0 && index < decalDatas.Count)
            {
                data = decalDatas[index];
                return true;
            }
            data = default;
            return false;
        }


        public virtual void SetDecal(DecalData data, int index)
        {
            if (index >= 0 && index < decalDatas.Count)
            {
                decalDatas[index] = data;
                OnColorChanged?.Invoke(this);
            }
        }

        public virtual void SetDecalSize(Vector4 size)
        {
            var material = SelectBlock();
            if (!material.HasProperty("_DecalScale")) return;
            material.SetVector("_DecalScale", size);
            UpdateMaterialBlock();
        }

        public virtual Vector4 GetDecalSize()
        {
            var material = SelectMaterial();
            if (!material.HasProperty("_DecalScale")) return new Vector4(0, 0, 0, 0);
            var v = material.GetVector("_DecalScale");


            return new Vector4(v.x, v.y, 0, 0);
        }

        public virtual void SetDecalColor(Color color, int index)
        {
            var material = SelectBlock();
            material.SetColor("_DecalColor_" + index, color);
            UpdateMaterialBlock();
        }

        public virtual Color GetDecalColor(int index)
        {
            var material = SelectMaterial();
            return material.GetColor("_DecalColor_" + index);

        }

        public virtual List<Color> GetDecalColors()
        {
            var material = SelectMaterial();
            var colors = new List<Color>();

            for (int i = 1; i < 4; i++)
            {
                colors.Add(material.GetColor("_DecalColor_" + i));
            }

            return colors;
        }

        public virtual void SetPattern(Texture texture)
        {
            pattern = texture;

            OnColorChanged?.Invoke(this);
        }

        public virtual void SetPattern(Texture texture, Color[] colors)
        {
            pattern = texture;

            for (int i = 0; i < colors.Length; i++)
            {
                if(i >= this.colors.Length) { break; }
                this.colors[i] = colors[i];
            }

            OnColorChanged?.Invoke(this);
        }

        public virtual Texture GetPattern()
        {
            return pattern;
        }

        public virtual void SetPatternColor(Color color, int index)
        {
            if (index >= this.colors.Length) { return; }
            colors[index] = color;
        }

        public virtual Color GetPatternColor(int index)
        {
            if (index >= this.colors.Length) { return colors[colors.Length - 1]; }
            return colors[index];
        }

        public virtual List<Color> GetPatternColors()
        {
            return colors.ToList();
        }

        public virtual void SetPatternSize(Vector2 size)
        {
            patternSize = size;
            OnColorChanged?.Invoke(this);
        }

        public virtual Vector4 GetPatternSize()
        {
            var material = SelectMaterial();
            if (!material.HasProperty("_PatternScale")) return new Vector4(0, 0, 0, 0);
            var v = material.GetVector("_PatternScale");

            return new Vector4(v.x, v.y, 0, 0);
        }

        public virtual void SetBaseTexture(Texture texture, Texture normalTexture = null)
        {

        }

        protected MaterialPropertyBlock SelectBlock()
        {
            if (controlMaterial) controlMaterial.GetPropertyBlock(block, controlIndex);
            else if (outfitRenderer) outfitRenderer.GetPropertyBlock(block);
            return block;
        }

        protected Material SelectMaterial()
        {
            Material mat = null;
            if (controlMaterial) mat = controlMaterial.sharedMaterials[controlIndex];
            else if (outfitRenderer) mat = outfitRenderer.sharedMaterial;
            else if (material) mat = material;
            return mat;
        }

        public void UpdateMaterialBlock()
        {
            if (controlMaterial) controlMaterial.SetPropertyBlock(block, controlIndex);
            else
            {
                if (outfitRenderer)
                {
                    outfitRenderer.SetPropertyBlock(block);
                    foreach (var item in skinnedRenderers)
                    {
                        item.SetPropertyBlock(block, controlIndex);
                    }
                }
            }
        }

        #endregion


        public OutfitData GetOutfitData()
        {
            var outfitData = new OutfitData();

            var path = Type.name + "/" + name;
            path = path.Replace("(Clone)", "");

            outfitData.outfit = path;

            if (customShader)
            {
                outfitData.color = GetColor(1);
                outfitData.swatch = currentSwatch;
            }
            else
            {
                outfitData.colors = GetColors();

                var pattern = GetPattern();
                if (pattern != null)
                {
                    outfitData.pattern = "Pattern/" + pattern.name;
                    outfitData.patternColors = GetPatternColors();
                    outfitData.patternScale = GetPatternSize();
                }
                else
                {
                    outfitData.pattern = "";
                }

            }

            var vis = new bool[optionalPieces.Length];
            for (int i = 0; i < optionalPieces.Length; i++)
            {
                if (optionalPieces[i] == null) continue;
                vis[i] = optionalPieces[i].activeSelf;
            }
            outfitData.partVisibility = vis;

            outfitData.decalDatas = decalDatas;

            return outfitData;
        }

        #region Soft Attach
#if UNITY_EDITOR
        public void SoftAttach()
        {
            var system = GetComponentInParent<OutfitSystem>();
            if (system == null)
            {
                //return bones if no longer attach to system
                SoftDetach();
                return;
            }
            system.SoftAttach(this);
        }

        public void SoftDetach()
        {
            editorAttachPoint = null;
            if (originalBones.Length > 0)
            {
                var skinnedRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
                if (skinnedRenderer == null) return;
                skinnedRenderer.bones = originalBones;
                skinnedRenderer.rootBone = originalRootBone;
                originalBones = new Transform[0];
                originalRootBone = null;
            }
        }
#endif
        #endregion

        #region Utility Scripts
        [ContextMenu("QuickName")]
        private void QuickName()
        {
            int underscoreIndex = name.IndexOf('_');
            string trimmed = underscoreIndex >= 0
                ? name.Substring(underscoreIndex + 1)
                : name;
            string spaced = Regex.Replace(trimmed, "(?<!^)([A-Z])", " $1");

            OutfitName = spaced;
        }
        #endregion

    }

    [System.Serializable]
    public class OutfitSwatch
    {
        public string swatchID;
        public Color IconColorTop = Color.white;
        public Color IconColorBottom = Color.black;
    }

    [System.Serializable]
    public class LinkedColorSets
    {
        public OutfitType linkedType;
        [Range(1, 9)] public int linkedChannelRange;
    }



}







