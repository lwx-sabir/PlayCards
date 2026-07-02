using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

namespace Bozo.ModularCharacters
{
    public class OutfitSystem : MonoBehaviour
    {
        //[Header("Save Data")]
        public CharacterObject characterData;
        private CharacterObject _characterData;
        public string SaveID;

        //[Header("Dependencies")]
        [SerializeField] SkinnedMeshRenderer CharacterBody;

        //Height
        public bool muteHeightChange { get; private set; }
        public bool mutebodyMods { get; private set; }
        public bool isbindPose { get; private set; }
        public Dictionary<GameObject, float> heightSources = new Dictionary<GameObject, float>();
        public float height { get; private set; }

        //Animation
        public Animator animator
        {
            get
            {
                if (_animator == null)
                {
                    _animator = GetComponentInParent<Animator>();
                    if (_animator == null) { _animator = GetComponentInChildren<Animator>(); }
                }

                return _animator;
            }
            private set
            { _animator = value; }
        }
        private Animator _animator;
        public float stance { get; private set; }

        //Bones
        public Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();
        public Dictionary<string, Transform> originalBoneMap = new Dictionary<string, Transform>();
        private Dictionary<Transform, BonePose> bindPose = new();

        //Outfits
        public List<Outfit> _Outfits = new List<Outfit>();
        public Dictionary<string, Outfit> Outfits = new Dictionary<string, Outfit>();
        public Dictionary<OutfitType, List<Outfit>> hiddenTypes = new Dictionary<OutfitType, List<Outfit>>();

        //Shapes
        private Dictionary<string, int> shapes = new Dictionary<string, int>();
        private Dictionary<string, float> shapesValues = new Dictionary<string, float>();
        private Dictionary<string, int> bodyShapes = new Dictionary<string, int>();
        private Dictionary<string, float> bodyShapesValues = new Dictionary<string, float>();
        private Dictionary<string, int> faceShapes = new Dictionary<string, int>();
        private Dictionary<string, float> faceShapesValues = new Dictionary<string, float>();
        private Dictionary<string, int> tagShapes = new Dictionary<string, int>();
        public Dictionary<string, BodyShapeModifier> bodyModifiers = new Dictionary<string, BodyShapeModifier>();
        private List<string> tags = new List<string>();


        //Events
        public UnityAction<Outfit> OnOutfitChanged;
        public UnityAction<Outfit> OnOutfitRemoved;
        public UnityAction<SkinnedMeshRenderer> OnRigChanged;
        public UnityAction<string, float> OnShapeChanged;
        public UnityAction<List<string>> OnTagsChanged;
        public UnityAction OnCharacterLoaded;


        //Textures and Materials
        private Dictionary<string, RenderTexture> textures = new Dictionary<string, RenderTexture>();
        private RenderTexture copyTexture;
        private RenderTexture normalCopyTexture;
        private Material masterMaterial;
        public Material copyMaterial;


        // Merged Properties
        public string prefabName;
        public Material mergeMaterial;

        public CharacterData data;
        private Dictionary<string, OutfitData> outfitData;
        public Dictionary<string, Texture> customMaps = new Dictionary<string, Texture>();
        public MergedMaterialData[] materialData;
        public List<RenderTexture> renderTextures = new List<RenderTexture>();

        //Mesh
        public Mesh combinedMesh;

        public enum LoadMode { OnStartAndOnValidate, OnStart, Manual }
        public LoadMode loadMode;

        public bool async;

        [Tooltip("Allows for this character to have the fully customized. Great for Main Characters that will change often or for the Character Creator. NOT RECOMMENDED for characters that will never change")]
        public bool isloading;
        public bool isUpdatingTexture;
        public bool cleanAfterMerge;
        public bool isMerging;
        public bool isStatic;
        public bool initalized { get; private set; }


#if MAGICACLOTH2
        //MagicaCloth
        private MagicaCloth2.ColliderComponent[] ClothColliders;
#endif


        private void OnValidate()
        {
            if (Application.isPlaying && gameObject.scene.isLoaded && loadMode == LoadMode.OnStartAndOnValidate && gameObject.activeSelf)
            {
                Invoke("LoadFromObject", 0f);
            }

        }

        private void Awake()
        {
            Init();
            if (loadMode == LoadMode.OnStart || loadMode == LoadMode.OnStartAndOnValidate)
            {
                LoadFromObject();
            }
        }

        private void Start()
        {
            InitClothColliders();
        }

        private void OnDestroy()
        {
            foreach (var item in renderTextures)
            {
                item.Release();
            }
            if(!isStatic)Destroy(combinedMesh);
            if (!isStatic) Destroy(masterMaterial);
            copyTexture.Release();

        }

        #region Initalizers

        public void Init()
        {
            if (initalized) return;
            if (CharacterBody == null)
            {
                Debug.LogWarning("Outfit System does not have a Rig assigned please assign one to prevent this warning", gameObject);
                Debug.LogWarning("Attempting auto rig assignment...");
                var skinnedMeshes = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var item in skinnedMeshes)
                {
                    if (item.name == "BMAC_Body")
                    {
                        CharacterBody = item;
                        Debug.Log("Rig Found Successfully!");
                        break;
                    }
                }
                Debug.LogError("Search Failed. Please Assign Mannually", gameObject);
                return;
            }

            InitBoneMap();
            InitBodyShapes();
            InitBodyMods();
            InitTextures();
            InitBindPose();

            initalized = true;
        }

        private void InitBoneMap()
        {
            boneMap.Clear();
            foreach (Transform bone in CharacterBody.bones)
            {
                if (boneMap.ContainsKey(bone.name) == false)
                {
                    boneMap.Add(bone.name, bone);
                }
            }

            originalBoneMap = new(boneMap);
        }

        public void InitTextures()
        {
            if (isStatic) return;

            masterMaterial = new Material(mergeMaterial);
            CharacterBody.sharedMaterial = masterMaterial;

            copyMaterial = new Material(Shader.Find("BoZo/BakeTexture"));
            copyTexture = new RenderTexture(2024, 2024, 0);
            copyTexture.wrapMode = TextureWrapMode.Repeat;

            var mrt = new RenderTexture(2024, 2024, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB);
            mrt.name = "MainTexture";
            masterMaterial.SetTexture("mainTexture", mrt);
            masterMaterial.mainTexture = mrt;
            textures["mainTexture"] = mrt;
            mrt.wrapMode = TextureWrapMode.Repeat;

            foreach (var data in materialData)
            {
                if (data.toMateiralProperty == "mainTexture") continue;
                var p = data.toMateiralProperty;
                RenderTexture rt;
                if (p.Contains("normal") || p.Contains("Normal") || p.Contains("bump") || p.Contains("Bump"))
                {
                    var desc = new RenderTextureDescriptor(2024, 2024)
                    {
                        graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                        depthBufferBits = 0,
                        msaaSamples = 1,
                        sRGB = false,
                        useMipMap = false,
                        autoGenerateMips = false,
                    };
                    rt = new RenderTexture(desc);
                    rt.wrapMode = TextureWrapMode.Repeat;
                }
                else
                {
                    rt = new RenderTexture(2024, 2024, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB);
                    rt.wrapMode = TextureWrapMode.Repeat;
                }

                rt.name = data.toMateiralProperty;
                rt.Create();

                textures[data.toMateiralProperty] = rt;


                masterMaterial.SetTexture(data.toMateiralProperty, rt);

            }
        }

        private void InitBodyShapes()
        {
            var body = GetOutfit("Body");
            var head = GetOutfit("Head");

            shapes.Clear();
            bodyShapes.Clear();
            faceShapes.Clear();
            tagShapes.Clear();

            var newShapes = new Dictionary<string, int>();

            if (body != null)
            {
                var blendShapeCount = body.skinnedRenderer.sharedMesh.blendShapeCount;

                for (int i = 0; i < blendShapeCount; i++)
                {
                    var blendName = body.skinnedRenderer.sharedMesh.GetBlendShapeName(i);

                    int dot = blendName.IndexOf('.');
                    if (dot >= 0) blendName = blendName.Substring(dot + 1);

                    int us = blendName.IndexOf('_');
                    if (us < 0) continue;

                    else if (string.CompareOrdinal(blendName, 0, "Shape_", 0, 6) == 0)
                    {
                        bodyShapes.Add(blendName.Substring(6), i);
                    }
                }
            }

            if(head != null)
            {
                var blendShapeCount = head.skinnedRenderer.sharedMesh.blendShapeCount;

                for (int i = 0; i < blendShapeCount; i++)
                {
                    var blendName = head.skinnedRenderer.sharedMesh.GetBlendShapeName(i);

                    int dot = blendName.IndexOf('.');
                    if (dot >= 0) blendName = blendName.Substring(dot + 1);

                    int us = blendName.IndexOf('_');
                    if (us < 0) continue;

                    else if (string.CompareOrdinal(blendName, 0, "Shape_", 0, 6) == 0)
                    {
                        faceShapes.Add(blendName.Substring(6), i);
                    }
                }
            }

            if (CharacterBody != null)
            {
                var blendShapeCount = CharacterBody.sharedMesh.blendShapeCount;             

                for (int i = 0; i < blendShapeCount; i++)
                {
                    var blendName = CharacterBody.sharedMesh.GetBlendShapeName(i);

                    int dot = blendName.IndexOf('.');
                    if (dot >= 0) blendName = blendName.Substring(dot + 1);

                    int us = blendName.IndexOf('_');
                    if (us < 0) continue;

                    else if (string.CompareOrdinal(blendName, 0, "Shape_", 0, 6) == 0)
                    {
                        var bsName = blendName.Substring(6);
                        shapes.Add(bsName, i);
                        if(!shapesValues.ContainsKey(bsName)) shapesValues.Add(bsName, 0);
                    }
                }
            }
        }

        public void InitBodyMods()
        {
            var bodyMods = new List<BodyShapeModifier>(GetComponentsInChildren<BodyShapeModifier>());
            bodyModifiers.Clear();
            for (int i = 0; i < bodyMods.Count; i++)
            {
               bodyModifiers[bodyMods[i].name] = bodyMods[i];
            }
        }


        public void InitBindPose()
        {
            bindPose.Clear();

            foreach (Transform bone in boneMap.Values)
            {
                bindPose[bone] = new BonePose
                {
                    localPosition = bone.localPosition,
                    localRotation = bone.localRotation,
                    localScale = bone.localScale
                };
            }
        }

        public void RestoreBindPose()
        {
            foreach (var pair in bindPose)
            {
                if (pair.Key == null)
                    continue;

                pair.Key.localPosition = pair.Value.localPosition;
                pair.Key.localRotation = pair.Value.localRotation;
                pair.Key.localScale = pair.Value.localScale;
            }
        }
        #endregion

        private void InitClothColliders()
        {
#if MAGICACLOTH2
            ClothColliders = GetComponentsInChildren<MagicaCloth2.ColliderComponent>();
#endif      
        }

        #region Saving and Loading

        public void LoadFromObject(CharacterObject saveData)
        {
            if (isloading) return;
            characterData = saveData;
            LoadFromObject();
        }

        [ContextMenu("Load")]
        public void LoadFromObject()
        {
            if (isloading) return;
            if (characterData)
            {
                if (_characterData != characterData)
                {
                    _characterData = characterData;
                    LoadCharacter(characterData.GetCharacterData());
                }
            }
        }

        [ContextMenu("LoadByID")]
        public void LoadFromID()
        {
            LoadFromID(SaveID);
        }

        public void LoadFromID(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) return;
            SaveID = saveName;

            var data = BMAC_SaveSystem.GetDataFromID(SaveID);
            if (data == null) return;

            LoadCharacter(data);
        }

        public void LoadCharacter(CharacterData data)
        {
            if(!isStatic) BMAC_SaveSystem.LoadCharacter(this, data, false, async);
        }

        [ContextMenu("SaveToObject")]
        public void SaveToObject()
        {
            if (!characterData)
            {
                Debug.LogWarning("Character Data Field is empty. Please provide a BSMC_CharacterObject to " + transform.name);
                return;
            }
            BMAC_SaveSystem.SaveCharacter(this, characterData.GetCharacterData().characterName, characterData.GetCharacterIcon());
        }

        [ContextMenu("SaveByID")]

        public void SaveByID()
        {
            SaveByID(SaveID);
        }

        public void SaveByID(string characterName)
        {
            if (string.IsNullOrEmpty(characterName))
            {
                Debug.LogWarning("No ID provided saving aborted");
                return;
            }

            //Creating EmptyIcon
            if (!System.IO.File.Exists(BMAC_SaveSystem.iconFilePath + "/" + characterName + ".png"))
            {
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                byte[] bytes = tex.EncodeToPNG();
                System.IO.File.WriteAllBytes(BMAC_SaveSystem.iconFilePath + "/" + characterName + ".png", bytes);
            }

            BMAC_SaveSystem.SaveCharacter(this, characterName);
        }

        #endregion


        #region Outfit Removeal
        public void RemoveOutfit(string type, bool merge = true)
        {
            var currentOutfitInSlot = GetOutfit(type);
            if (currentOutfitInSlot != null) RemoveOutfit(currentOutfitInSlot.Type, merge);
        }


        public async void RemoveOutfit(OutfitType type, bool merge = true)
        {
            if (Outfits.TryGetValue(type.name, out Outfit currentOutfitInSlot))
            {
                if (currentOutfitInSlot != null)
                {
                    foreach (var bone in currentOutfitInSlot.additionalBones)
                    {
                        foreach (Transform child in bone.GetComponentsInChildren<Transform>(true))
                        {
                            boneMap.Remove(child.name);
                        }
                    }

                    RemoveHide(currentOutfitInSlot);
                    currentOutfitInSlot.ReturnBones();
                    currentOutfitInSlot.transform.parent = null;
                    DestroyImmediate(currentOutfitInSlot.gameObject);
                    Outfits[type.name] = null;
                    _Outfits.Remove(currentOutfitInSlot);
                }
            }
            AddTags();
            OnOutfitChanged?.Invoke(null);

           if(merge) MergeCharacter();
        }

        public void RemoveTags(string[] outfitTags)
        {
            foreach (var item in outfitTags)
            {
                tags.Remove(item);
            }
            OnTagsChanged?.Invoke(tags);
            //tags.RemoveAll(item => removedOutfit.tags.Contains(item));
        }

        public async void RemoveAllOutfits(bool returnBones = true)
        {
            var o = new List<Outfit>(Outfits.Values);

            Outfits.Clear();
            _Outfits.Clear();
            tags.Clear();
            hiddenTypes.Clear();
            boneMap = new(originalBoneMap);

            
            foreach (var item in o)
            {
                if (item == null) continue;
                if (returnBones) item.ReturnBones();
                Destroy(item.gameObject);
            }
        }
        #endregion

        //Legacy Method
        public void AttachSkinnedOutfit(Outfit outfit)
        {
            AttachOutfit(outfit);
        }

        public async void AttachOutfit(Outfit outfit)
        {
            if (!initalized || isStatic) return;

            outfit.transform.parent = transform;
            outfit.transform.SetPositionAndRotation(transform.position, transform.rotation);
            outfit.transform.localScale = (Vector3.one);
            if(outfit.mergeMesh) outfit.gameObject.SetActive(false);

            //check if an outfit is already in that slot and replace it
            RemoveOutfit(outfit.Type, false);
            _Outfits.Add(outfit);
            Outfits[outfit.Type.name] = outfit;
            //Merging outfit bones or attaching outfit to specified bone
            MergeBones(outfit);


            outfit.OnColorChanged += UpdateTexture;
            outfit.OnOutfitChanged += MergeCharacter;

            //Apply the Current Body Morphs to the Outfit
            ApplyShapesToOufit(outfit);

            //If Head get its Morphs
            if (outfit.Type.name == "Head" && outfit.Type.name == "Body") { InitBodyMods(); InitBodyShapes(); }

            AddTags();
            SetHide(outfit);

            OnOutfitChanged?.Invoke(outfit);

            if (isMerging || isloading) return;
            MergeCharacter();
        }

        private async void UpdateTexture(Outfit outfit)
        {
            if (isUpdatingTexture || isMerging || isloading || hiddenTypes.ContainsKey(outfit.Type)) return;
            isUpdatingTexture = true;

            await Task.Delay(50);

            var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
            float progress = stateInfo.normalizedTime;
            int stateHash = stateInfo.fullPathHash;
            animator.enabled = false;

            MuteBodyMods(true);
            RestoreBindPose();



            if (outfit.materialPriority != 0)
            {
                MergeTextures();

                animator.enabled = true;
                animator.Play(stateHash, 0, progress % 1);
                MuteBodyMods(false);
                isUpdatingTexture = false;
                return;
            }

            var rt = textures["mainTexture"];
            Graphics.Blit(rt, copyTexture);

            copyMaterial.mainTexture = rt;
            copyMaterial.SetTexture("_BlendTex", outfit.material.mainTexture);

            copyMaterial.SetFloat("_UseCustomColors", 1);
            copyMaterial.SetFloat("_isNormalMap", 0);

            copyMaterial.SetTexture("_IDMap", outfit.material.GetTexture("_IDMap"));
            if (copyMaterial.GetTexture("_IDMap") == null) copyMaterial.SetFloat("_UsingIDMap", 0);
            else copyMaterial.SetFloat("_UsingIDMap", 1);

            for (int i = 0; i < outfit.colors.Length; i++)
            {
                var index = i + 1;
                copyMaterial.SetColor("_Color_" + index, outfit.colors[i]);
            }

            //Pattern
            copyMaterial.SetTexture("_PatternMap", outfit.pattern);
            copyMaterial.SetVector("_PatternScale", outfit.patternSize);
            for (int i = 0; i < outfit.patternColors.Length; i++)
            {
                var index = i + 1;
                copyMaterial.SetColor("_PatternColor_" + index, outfit.colors[i]);
            }

            Graphics.Blit(rt, copyTexture, copyMaterial);
            Graphics.Blit(copyTexture, rt);

            outfit.ApplyDecals(rt, copyTexture);

            animator.enabled = true;
            animator.Play(stateHash, 0, progress % 1);

            MuteBodyMods(false);
            isUpdatingTexture = false;
        }

        private void MergeTextures()
        {
            var rt = textures["mainTexture"];

            var activeRT = RenderTexture.active;
            RenderTexture.active = copyTexture;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = activeRT;

            _Outfits.Sort((a, b) => b.materialPriority.CompareTo(a.materialPriority));

            foreach (var o in _Outfits)
            {
                if (!o.mergeTexture) continue;
                if (hiddenTypes.ContainsKey(o.Type)) continue;

                copyMaterial.mainTexture = rt;
                copyMaterial.SetTexture("_BlendTex", o.material.mainTexture);

                copyMaterial.SetFloat("_UseCustomColors", 1);
                copyMaterial.SetFloat("_isNormalMap", 0);

                copyMaterial.SetTexture("_IDMap", o.material.GetTexture("_IDMap"));
                if (copyMaterial.GetTexture("_IDMap") == null) copyMaterial.SetFloat("_UsingIDMap", 0);
                else copyMaterial.SetFloat("_UsingIDMap", 1);

                for (int i = 0; i < o.colors.Length; i++)
                {
                    var index = i + 1;
                    copyMaterial.SetColor("_Color_" + index, o.colors[i]);
                }

                //Pattern
                copyMaterial.SetTexture("_PatternMap", o.pattern);
                copyMaterial.SetVector("_PatternScale", o.patternSize);
                for (int i = 0; i < o.patternColors.Length; i++)
                {
                    var index = i + 1;
                    copyMaterial.SetColor("_PatternColor_" + index, o.colors[i]);
                }

                Graphics.Blit(rt, copyTexture, copyMaterial);
                Graphics.Blit(copyTexture, rt);

                o.ApplyDecals(rt, copyTexture);
            }


            foreach (var data in materialData)
            {
                var p = data.toMateiralProperty;
                if (p == "mainTexture")
                {
                    continue;
                }
                rt = textures[p];

                activeRT = RenderTexture.active;
                RenderTexture.active = copyTexture;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = rt;
                GL.Clear(true, true, data.backgroundColor);

                RenderTexture.active = activeRT;

                foreach (var o in _Outfits)
                {
                    if (!o.mergeTexture) continue;
                    copyMaterial.mainTexture = rt;
                    copyMaterial.SetTexture("_BlendTex", o.material.GetTexture(p));

                    copyMaterial.SetFloat("_UseCustomColors", 0);
                    copyMaterial.SetFloat("_UsingIDMap", 1);
                    copyMaterial.SetTexture("_IDMap", o.material.mainTexture); //Using MainTexture as a mask

                    if (p.Contains("normal") || p.Contains("Normal") || p.Contains("bump") || p.Contains("Bump"))
                    {
                        if (o.material.GetTexture(p) == null) continue;
                        copyMaterial.SetFloat("_isNormalMap", 1);
                    }
                    else { copyMaterial.SetFloat("_isNormalMap", 0); }

                    Graphics.Blit(rt, copyTexture, copyMaterial);
                    Graphics.Blit(copyTexture, rt);
                }
            }

        }

        private void ApplyShapesToOufit(Outfit outfit)
        {
            var keys = new List<string>(bodyShapes.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                GetBodyShapeValues();
                outfit.SetShape(keys[i], GetShape(keys[i]));
            }
        }

        private void ApplyAllShapes()
        {
            foreach (var shape in shapesValues)
            {
                if (shapes.TryGetValue(shape.Key, out int bodyIndex))
                {
                    CharacterBody.SetBlendShapeWeight(bodyIndex, shape.Value);
                }
            }
        }

        public void ResetShapes()
        {
            foreach (var item in shapes)
            {
                SetShape(item.Key, 0);
            }
        }

        public void SetShape(string key, float value)
        {
            SkinnedMeshRenderer renderer = CharacterBody;
            var index = -1;


            if (shapes.TryGetValue(key, out int bodyValue) && renderer)
            {
                index = bodyValue;
            }

            if (index != -1)
            {
                renderer.SetBlendShapeWeight(index, value);
                shapesValues[key] = value;
            }

            OnShapeChanged?.Invoke(key, value);
        }

        public void AddTags()
        {
            tags.Clear();
            foreach (var o in _Outfits)
            {
                if (hiddenTypes.ContainsKey(o.Type)) continue;
                tags.AddRange(o.tags);
                foreach (var t in o.GetComponentsInChildren<ApplyTags>(true))
                {
                    if (t.gameObject.activeSelf) tags.AddRange(t.tags);
                }
            }

            OnTagsChanged?.Invoke(this.tags);
        }

        public void SetHide(Outfit outfit)
        {
            if (outfit == null) return;
            foreach (var item in outfit.HideTypes)
            {
                if (hiddenTypes.ContainsKey(item))
                {
                    hiddenTypes[item].Add(outfit);
                }
                else
                {
                    hiddenTypes.Add(item, new List<Outfit>());
                    hiddenTypes[item].Add(outfit);
                }
                var hidden = GetOutfit(item);
                if (hidden) hidden.gameObject.SetActive(false);
            }

        }

        public void RemoveHide(Outfit outfit)
        {
            if (outfit == null) return;
            foreach (var item in outfit.HideTypes)
            {
                if (hiddenTypes.ContainsKey(item))
                {
                    hiddenTypes[item].Remove(outfit);
                    if (hiddenTypes[item].Count == 0)
                    {
                        hiddenTypes.Remove(item);
                    }
                }
            }
        }

        private void ApplyTags()
        {
            /*
            var shapes = new List<string>(tagShapes.Keys);
            if (!CharacterBody) return;
            if (CharacterBody.sharedMesh.blendShapeCount == 0) return;
            for (int i = 0; i < shapes.Count; i++)
            {
                var yes = ContainsTag(shapes[i]);
                if (yes) { CharacterBody.SetBlendShapeWeight(tagShapes[shapes[i]], 100); }
                else { CharacterBody.SetBlendShapeWeight(tagShapes[shapes[i]], 0); }
            }
            */
        }

        public void SetStance(float value)
        {
            foreach (AnimatorControllerParameter param in animator.parameters)
            {
                if (param.name == "Stance") animator.SetFloat("Stance", value);
            }
            stance = value;
        }

        public void AddHeightSource(GameObject source, float value)
        {
            heightSources[source] = value;
            SetHeight();
        }

        public void RemoveHeightSource(GameObject source, float value)
        {
            heightSources.Remove(source);
            SetHeight();
        }

        public void SetHeight()
        {
            if (muteHeightChange) return;


            transform.localPosition = new Vector3(transform.localPosition.x, transform.localPosition.y - height, transform.localPosition.z);

            height = 0f;
            foreach (var value in heightSources)
            {
                height += value.Value;
            }
            transform.localPosition = new Vector3(transform.localPosition.x, transform.localPosition.y + height, transform.localPosition.z);

        }

        public void MuteHeightChange()
        {
            MuteHeightChange(!muteHeightChange);
        }

        public void MuteHeightChange(bool value)
        {
            if (value == muteHeightChange) return;


            if(muteHeightChange) transform.localPosition = new Vector3(transform.localPosition.x, transform.localPosition.y - height, transform.localPosition.z);
            SetHeight();
        }

        public void MuteBodyMods()
        {
            mutebodyMods = !mutebodyMods;
            MuteBodyMods(mutebodyMods);
        }

        public void MuteBodyMods(bool mute)
        {
            foreach (var mod in bodyModifiers.Values)
            {
                if(mod != null) mod.SetMute(mute);
            }
            mutebodyMods = mutebodyMods;
        }

        public void ForceBindPose()
        {
            ForceBindPose(!isbindPose);
        }

        public void ForceBindPose(bool bindPose)
        {
            if (isbindPose && animator)
            {
                animator.enabled = false;
                RestoreBindPose();
            }
            else
            {
                animator.enabled = true;
            }

            this.isbindPose = bindPose;
        }



        private void MergeBones(Outfit outfit)
        {
            if (outfit.additionalBones.Length != 0)
            {
                for (int i = 0; i < outfit.additionalBones.Length; i++)
                {
                    Transform bone = outfit.additionalBones[i];

                    if (bone == null || bone.parent == null)
                        continue;

                    // Find the matching body bone that this extra chain should attach to.
                    if (!GetBones().TryGetValue(bone.parent.name, out Transform newParent))
                    {
                        Debug.LogWarning($"Missing parent bone '{bone.parent.name}' for additional bone '{bone.name}'");
                        continue;
                    }

                    // If an old extra bone with the same name exists, remove it.
                    // This prevents Hair B from using Hair A's Hair_01 / Hair_02 transforms.
                    if (boneMap.TryGetValue(bone.name, out Transform oldBone) && oldBone != null && oldBone != bone)
                    {
                        Destroy(oldBone.gameObject);
                        boneMap.Remove(bone.name);
                    }

                    // Attach the new extra bone chain to the real body bone.
                    // Usually false is correct if the hair was authored under a duplicate Head/Neck/etc.
                    bone.SetParent(newParent, false);

                    foreach (Transform item in bone.GetComponentsInChildren<Transform>(true))
                    {
                        if (boneMap.TryGetValue(item.name, out Transform existing) && existing != null && existing != item)
                        {
                            Destroy(existing.gameObject);
                        }

                        boneMap[item.name] = item;
                    }
                }
            }

            foreach (var smr in outfit.skinnedRenderers)
            {
                var renderer = smr;

                if (outfit.AttachPoint == "" && renderer)
                {
                    var oldBones = renderer.bones.ToArray();
                    var newBones = new Transform[renderer.bones.Length];
                    for (int i = 0; i < oldBones.Length; i++)
                    {
                        var bone = oldBones[i];
                        if (bone == null) continue;
                        boneMap.TryGetValue(bone.name, out Transform baseBone);
                        newBones[i] = baseBone;
                    }
                    renderer.bones = newBones;
                    renderer.rootBone = CharacterBody.rootBone;
                }
                else
                {
                    Transform bone = null;
                    try
                    {
                        bone = boneMap[outfit.AttachPoint];
                    }
                    catch
                    {
                        Debug.LogError(name + " is missing " + outfit.AttachPoint + " that " + outfit.name + " requires");
                        return;
                    }


                    //outfit.transform.parent = bone.transform;
                    //outfit.transform.position = bone.position;
                    //outfit.transform.rotation = bone.rotation;
                    //outfit.transform.localScale = Vector3.one;
                }
            }

            outfit.Initalized = true;

            if (outfit.outfitRenderer && outfit.AttachPoint != "")
            {
                Transform bone = null;
                try
                {
                    bone = boneMap[outfit.AttachPoint];
                }
                catch
                {
                    Debug.LogError(name + " is missing " + outfit.AttachPoint + " that " + outfit.name + " requires");
                    return;
                }


                outfit.transform.parent = bone.transform;
                outfit.transform.position = bone.position;
                outfit.transform.rotation = bone.rotation;
                outfit.transform.localScale = Vector3.one;
            }
        }

        public bool ContainsTag(string tag)
        {
            return tags.Contains(tag);
        }

        #region Getters

        public Outfit GetOutfit(OutfitType outfitType)
        {
            if (Outfits.TryGetValue(outfitType.name, out Outfit item))
            {
                return item;
            }
            return null;
        }

        public Outfit GetOutfit(string outfitType)
        {

            if (Outfits.TryGetValue(outfitType, out Outfit item))
            {
                return item;
            }
            return null;
        }

        public List<Outfit> GetOutfits()
        {
            return new List<Outfit>(Outfits.Values);
        }

        public Dictionary<string, int> GetShapes()
        {
            return shapes;
        }

        public Dictionary<string, int> GetBodyShapes()
        {
            return bodyShapes;
        }

        public Dictionary<string, int> GetFaceShapes()
        {
            return faceShapes;
        }

#if MAGICACLOTH2
        public MagicaCloth2.ColliderComponent[] GetClothColliders()
        {
            return ClothColliders;
        }
#endif

        public float GetShape(string key)
        {
            if (bodyShapes.TryGetValue(key, out int value))
            {
                var body = GetOutfit("Body");
                if (body != null)
                {
                    var weightValue = body.skinnedRenderer.GetBlendShapeWeight(value);
                    return weightValue;
                }
                else return -10000;
            }
            else return -10000;
        }

        public Dictionary<string, BodyShapeModifier> GetMods()
        {
            return bodyModifiers;
        }
        public Dictionary<string, Transform> GetBones()
        {
            return boneMap;
        }

        public float GetShapeValue(string key)
        {
            var weight = -1f;

            var body = GetOutfit("Body");

            if (body == null) return -1;
            if (bodyShapes.TryGetValue(key, out int bodyValue))
            {
                weight = body.skinnedRenderer.GetBlendShapeWeight(bodyValue);
            }
            else if (faceShapes.TryGetValue(key, out int faceValue))
            {
                var face = GetOutfit("Head");
                if (face == null) return -1;
                weight = face.skinnedRenderer.GetBlendShapeWeight(faceValue);
            }

            return weight;
        }

        public float GetShapeValue(int key)
        {
            SkinnedMeshRenderer renderer;
            var body = GetOutfit("Body");
            if (body == null) renderer = CharacterBody;
            else renderer = body.skinnedRenderer;

            var weightValue = renderer.GetBlendShapeWeight(key);
            return weightValue;
        }

        public Dictionary<string, float> GetBodyShapeValues()
        {
            var bodyShapeValues = new Dictionary<string, float>();
            var shapes = bodyShapes.Values.ToArray();
            var keys = bodyShapes.Keys.ToArray();

            SkinnedMeshRenderer renderer;
            var body = GetOutfit("Body");
            if (body == null) renderer = CharacterBody;
            else renderer = body.skinnedRenderer;

            for (int i = 0; i < shapes.Length; i++)
            {
                var weightValue = renderer.GetBlendShapeWeight(shapes[i]);
                bodyShapeValues.Add(keys[i], weightValue);
            }

            return bodyShapeValues;
        }

        public Dictionary<string, float> GetFaceShapeValues()
        {
            var faceShapeValues = new Dictionary<string, float>();
            var shapes = faceShapes.Values.ToArray();
            var keys = faceShapes.Keys.ToArray();

            SkinnedMeshRenderer renderer;
            var head = GetOutfit("Head");
            if (head == null) renderer = CharacterBody;
            else renderer = head.skinnedRenderer;


            for (int i = 0; i < shapes.Length; i++)
            {
                var weightValue = renderer.GetBlendShapeWeight(shapes[i]);
                faceShapeValues.Add(keys[i], weightValue);
            }

            return faceShapeValues;
        }

        public SkinnedMeshRenderer GetCharacterBody() { return CharacterBody; }
        #endregion

#if UNITY_EDITOR

        public void SoftAttach(Outfit outfit)
        {
            //For Attaching outfits during in the Editor 
            if (CharacterBody == null)
            {
                Debug.LogWarning("Soft Attach attempted but OuftitSystem did not have a CharacterBody please assign in the inspector", gameObject);
                return;
            }

            Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();
            foreach (Transform bone in CharacterBody.bones)
            {
                if (boneMap.ContainsKey(bone.name) == false)
                {
                    boneMap.Add(bone.name, bone);
                }
            }

            var renderers = outfit.GetComponentsInChildren<SkinnedMeshRenderer>();


            //Already Attached
            if (outfit.originalBones.Length > 0)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].localBounds = CharacterBody.localBounds;

                if (outfit.AttachPoint == "" && renderers[i])
                {
                    if (outfit.Initalized == false)
                    {
                        outfit.originalBones = renderers[i].bones;
                        outfit.originalRootBone = renderers[i].rootBone;

                        var oldBones = renderers[i].bones.ToArray();
                        var newBones = new Transform[renderers[i].bones.Length];
                        for (int b = 0; b < oldBones.Length; b++)
                        {
                            var bone = oldBones[b];
                            boneMap.TryGetValue(bone.name, out newBones[b]);
                        }
                        renderers[i].bones = newBones;
                        renderers[i].rootBone = CharacterBody.rootBone;

                    }
                }
            }

        }
#endif


        public void SetCharacterBody(GameObject newBody)
        {
            /*
            if (newBody == null) return;
            var smr = newBody.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return;

            RemoveAllOutfits();
            DestroyImmediate(CharacterBody.transform.parent.gameObject);

            newBody.transform.parent = transform;
            newBody.transform.localPosition = Vector3.zero;
            newBody.transform.localRotation = Quaternion.identity;
            newBody.transform.localScale = Vector3.one;

            CharacterBody = smr;


            InitBoneMap();
            InitBodyShapes();
            InitBodyMods();
            InitClothColliders();

            BMAC_SaveSystem.LoadBodyMods(this, data);
            OnRigChanged?.Invoke(CharacterBody);

            Invoke("RebindBody", 0);

            SetStance(data.stance);
            */
        }

        public void SetRenderTextures(List<RenderTexture> rt)
        {
            foreach (var item in renderTextures)
            {
                item.Release();
            }
            renderTextures = new List<RenderTexture>(rt);
        }

        public void MergeCharacter(Outfit outfit)
        {
            MergeCharacter();
        }

        [ContextMenu("Merge")]
        public async Task MergeCharacter()
        {
            if (isStatic) return;

                if (isMerging || isloading) return;
                isMerging = true;
                await Task.Yield();

            if (Application.isPlaying && gameObject.scene.isLoaded)
            {
                //Remembering Animation State
                
                var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                var control = animator.runtimeAnimatorController;
                var avatar = animator.avatar;
                float progress = stateInfo.normalizedTime;
                int stateHash = stateInfo.fullPathHash;
                bool wasEnabled = animator.enabled;
                animator.enabled = false;


                animator.runtimeAnimatorController = null;
                

                _Outfits.RemoveAll(x => x == null);

#if MAGICACLOTH2
                customMaps.Remove("MagicaClothExtension");
                try
                {
                    foreach (var item in GetComponentsInChildren<BoZo_MagicaClothSupport>())
                    {
                        item.DestroyCloth();
                    }
                }
                catch
                {
                    Debug.LogWarning("Magica related things has failed");
                }
#endif

                //Muting Body Changes as they mess up the merge process
                MuteBodyMods(true);
                MuteHeightChange(true);

                //Adding Tags
                AddTags();

                //Merging Textures
                try
                {
                    MergeTextures();
                }
                catch
                {
                    Debug.LogError("Texture Merge has failed");
                }

                var optimizer = new BoZo_CharacterOptimizer();


                //RestoreBindPose();
                try
                {
                    //Mesh Merging
                    optimizer.MergeButTheBetterOne(this);
                }
                catch
                {
                    Debug.LogError("Character Merge Failed");
                }


                try
                {
                    //Returning Body Shapes
                    MuteBodyMods(false);
                    MuteHeightChange(false);

                    //Gathering blendshapes
                    InitBodyShapes();
                    InitBodyMods();
                    ApplyAllShapes();
                }
                catch
                {
                    Debug.LogError("Body Shapes has failed");
                }


                //Returning Animation State
                animator.Rebind();
                animator.avatar = avatar;
                animator.runtimeAnimatorController = control;
                animator.enabled = wasEnabled;
                animator.Play(stateHash, 0, progress % 1);
                

                if (cleanAfterMerge)
                {
                    RemoveAllOutfits();
                    isStatic = true;
                }

                OnRigChanged?.Invoke(CharacterBody);

            }
            else
            {
                Debug.LogWarning("For stability reasons Character Merging is only available in Play Mode");
            }
                isMerging = false;

        }


        [ContextMenu("SaveToPrefab")]
        public async void SaveCharacterToPrefab()
        {
#if UNITY_EDITOR

            animator.Rebind();

            RemoveAllOutfits(false);
            await Task.Yield();


#if MAGICACLOTH2
            foreach (var item in GetComponentsInChildren<MagicaCloth2.MagicaCloth>())
            {
                item.enabled = false;
            }
            foreach (var item in GetComponentsInChildren<BoZo_MagicaClothSupport>())
            {
                DestroyImmediate(item);
            }
#endif

            var settings = CharacterToolSettingsProvider.Get();

            var path = settings.prefabFolder;


            var saveName = prefabName;
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
            AssetDatabase.CreateAsset(CharacterBody.sharedMesh, meshPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

            //Saving Materials and Textures
            var matList = new List<Material>();

            foreach (var item in CharacterBody.sharedMaterials)
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

                foreach (var matData in materialData)
                {
                    if (matData.toMateiralProperty == "mainTexture") continue;

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

                    if (matData.toMateiralProperty.Contains("normal") || matData.toMateiralProperty.Contains("Normal") || matData.toMateiralProperty.Contains("bump") || matData.toMateiralProperty.Contains("Bump"))
                    {
                        TextureImporter importer = AssetImporter.GetAtPath($"{assetPath}/{mat.name}{matData.toMateiralProperty}.png") as TextureImporter;
                        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
                        {
                            Debug.LogWarning("Prefabs made when build target is set to android do not need their Normal Maps set to Normal in their Import settings. Doing so will result in unwanted lighting");
                            importer.sRGBTexture = false;
                            importer.SaveAndReimport();
                        }
                        else
                        {

                            importer.textureType = TextureImporterType.NormalMap;
                            importer.sRGBTexture = false;
                            importer.mipmapEnabled = true;
                            importer.SaveAndReimport();
                        }
                    }

                }


                //Saving Material
                string matPath = $"{assetPath}/{mat.name}_Mat.mat";
                AssetDatabase.CreateAsset(mat, matPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                matList.Add(AssetDatabase.LoadAssetAtPath<Material>(matPath));
            }

            CharacterBody.sharedMaterials = matList.ToArray();

            isStatic = true;
            _characterData = null;
            characterData = null;
            SaveID = "";

            var iconCam = GetComponentInChildren<Camera>();
            if (iconCam) DestroyImmediate(iconCam.gameObject);

            // Save the prefab
            string prefabPath = $"{assetPath}/{saveName}.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(gameObject, prefabPath);
            var prefabSkinnedMeshrenderer = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
            prefabSkinnedMeshrenderer.sharedMesh = mesh;

#if MAGICACLOTH2
            foreach (var item in prefab.GetComponentsInChildren<MagicaCloth2.MagicaCloth>())
            {
                item.enabled = true;
            }
#endif


            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Saved Prefab at: {prefabPath}");


#endif


        }
        private struct BonePose
        {
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
        }
    }
}
