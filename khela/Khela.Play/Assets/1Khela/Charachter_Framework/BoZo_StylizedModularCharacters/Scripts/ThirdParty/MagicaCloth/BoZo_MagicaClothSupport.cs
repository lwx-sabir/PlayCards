using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Bozo.ModularCharacters
{
    public class BoZo_MagicaClothSupport : MonoBehaviour, IOutfitExtension<Texture>
    {
        public const string id = "MagicaClothExtension";

        bool initalized;
        private OutfitSystem system;
        public ClothType type;
        public SkinnedMeshRenderer skinnedMeshRenderer;
        public string[] disableByTag;
        public bool InitalizeOnStart;

        [Header("Bones")]
        public bool boneReferenceByString;
        public List<Transform> rootBones;
        public List<string> rootBonesString;
        public float collisionSize = 0.025f;
        public AnimationCurve collisionCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 1));

        [Header("Mesh")]
        public Texture2D influenceMap;
        private List<Texture2D> influenceMaps = new List<Texture2D>();
        [Range(0, 0.2f)] public float reductionSetting = 0.065f;

        [Header("Preset")]
        public TextAsset clothPreset;

        public List<Transform> transforms = new List<Transform>();

#if MAGICACLOTH2
        MagicaCloth2.MagicaCloth cloth;
#endif

        public Texture GetValue() => influenceMap;
        object IOutfitExtension.GetValue() => influenceMap;
        public System.Type GetValueType() => typeof(Texture);

        private void Awake()
        {
#if MAGICACLOTH2

            foreach (var item in GetComponents<MagicaCloth2.MagicaCloth>())
            {
                if (item != cloth) Destroy(item);
            }
#endif
        }

        private void OnEnable()
        {
            system = GetComponentInParent<OutfitSystem>();
            if (system) system.OnRigChanged += OnCharacterMerged;
            if (system) system.OnOutfitChanged += DisableClothByTag;
        }

        private void OnDisable()
        {
            system = GetComponentInParent<OutfitSystem>();
            if (system) system.OnRigChanged -= OnCharacterMerged;
            if (system) system.OnOutfitChanged -= DisableClothByTag;
        }

        private void Start()
        {
            if (InitalizeOnStart)
            {
                system = GetComponentInParent<OutfitSystem>();

                Initalize(system, null);
                Execute(system, null);
            }

        }

        public void Initalize(OutfitSystem outfitSystem, Outfit outfit)
        {
#if MAGICACLOTH2

            if (type == ClothType.Mesh && influenceMap == null) ;
            if (cloth) Destroy(cloth);
            cloth = gameObject.AddComponent<MagicaCloth2.MagicaCloth>();

            cloth.Initialize();
            cloth.DisableAutoBuild();
#endif
        }

        private async void OnCharacterMerged(SkinnedMeshRenderer rig)
        {
#if MAGICACLOTH2
            try
            {
                influenceMaps.Clear();
                influenceMap = null;
                if (cloth) { DestroyImmediate(cloth); cloth = null; }
                if (system == null) return;

                foreach (var item in system.GetOutfits())
                {
                    if (item == null) continue;
                    if (item.MC2Map && !system.hiddenTypes.ContainsKey(item.Type)) influenceMap = item.MC2Map;
                }

                initalized = false;


                Initalize(system, null);
                Execute(system, null);
            }
            catch
            {
                Debug.LogError("Magica Failed to Initalize After Character Merge");
            }
#endif
        }

        public void DestroyCloth()
        {
#if MAGICACLOTH2
            if (cloth) Destroy(cloth);
            influenceMap = null;
#endif  
        }

        private void DisableClothByTag(Outfit outfit)
        {
#if MAGICACLOTH2
            if (system == null || !initalized) return;
            var enabled = true;
            foreach (var item in disableByTag)
            {
                if (system.ContainsTag(item)) enabled = false;
            }
            if (cloth) cloth.enabled = enabled;
#endif
        }

        public void Execute(OutfitSystem outfitSystem, Outfit outfit)
        {
#if MAGICACLOTH2
            if (outfitSystem)
            {
                //if (outfitSystem.mergeBase) return;
            }

            if (initalized) return;

            system = outfitSystem;
            switch (type)
            {
                case ClothType.Mesh:
                    SetMeshCloth(outfitSystem, outfit);
                    break;
                case ClothType.Bone:
                    SetBoneCloth(outfitSystem, outfit);
                    break;
                case ClothType.Spring:
                    SetBoneCloth(outfitSystem, outfit);
                    break;
                default:
                    break;
            }

            initalized = true;
            if (outfit) DisableClothByTag(outfit);
#endif
        }

#if MAGICACLOTH2
        private void SetMeshCloth(OutfitSystem outfitSystem, Outfit outfit)
        {
            if (influenceMap == null) return;
            var sdata = cloth.SerializeData;

            SkinnedMeshRenderer smr = null;

            if (skinnedMeshRenderer)
            {
                smr = skinnedMeshRenderer;
            }
            else
            {
                if (outfit) smr = outfit.skinnedRenderer;
                else smr = outfitSystem.GetCharacterBody();
            }

            sdata.sourceRenderers.Add(smr);

            sdata.reductionSetting.shapeDistance = reductionSetting;

            sdata.paintMode = MagicaCloth2.ClothSerializeData.PaintMode.Texture_Fixed_Move;


            sdata.paintMaps.Add((Texture2D)influenceMap);



            if (clothPreset) cloth.SerializeData.ImportJson(clothPreset.ToString());
            sdata.radius = new MagicaCloth2.CurveSerializeData(collisionSize, collisionCurve);

            MagicaCloth2.ColliderComponent[] col = null;
            if (outfitSystem)
            {
                col = outfitSystem.GetClothColliders();
            }
            else
            {
                col = skinnedMeshRenderer.GetComponentsInChildren<MagicaCloth2.ColliderComponent>();
            }
            sdata.colliderCollisionConstraint.colliderList = col.ToList();
            cloth.enabled = true;
            cloth.BuildAndRun();
        }

        private void SetBoneCloth(OutfitSystem outfitSystem, Outfit outfit)
        {
            var sdata = cloth.SerializeData;
            if (type == ClothType.Bone) sdata.clothType = MagicaCloth2.ClothProcess.ClothType.BoneCloth;
            if (type == ClothType.Spring) sdata.clothType = MagicaCloth2.ClothProcess.ClothType.BoneSpring;


            SkinnedMeshRenderer smr = null;

            if (skinnedMeshRenderer)
            {
                smr = skinnedMeshRenderer;
            }
            else
            {
                if (outfit) smr = outfit.skinnedRenderer;
                else smr = outfitSystem.GetCharacterBody();
            }

            if (rootBonesString.Count != 0)
            {
                sdata.rootBones = smr.bones.Where(bone => rootBonesString.Contains(bone.name)).ToList();
            }
            else
            {
                sdata.rootBones = rootBones;
            }

            if (clothPreset) cloth.SerializeData.ImportJson(clothPreset.ToString());
            sdata.radius = new MagicaCloth2.CurveSerializeData(collisionSize, collisionCurve);

            MagicaCloth2.ColliderComponent[] col = null;
            if (outfitSystem)
            {
                col = outfitSystem.GetClothColliders();
            }
            else
            {
                col = skinnedMeshRenderer.GetComponentsInChildren<MagicaCloth2.ColliderComponent>();
            }

            if (col != null) sdata.colliderCollisionConstraint.colliderList = col.ToList();




            cloth.enabled = true;
            cloth.BuildAndRun();
        }
#endif

        public string GetID()
        {
            return id;
        }

        public enum ClothType { Mesh, Bone, Spring }
    }
}
