
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    [CreateAssetMenu(fileName = "BSMC_CharacterObject", menuName = "BSMC_CharacterObject")]
    public class BSMC_CharacterObject : DataObject
    {

        [SerializeField] List<OufitParam> outfits = new List<OufitParam>();
        public List<Color> SkinColor = new List<Color>();
        public List<Color> EyeColor = new List<Color>();
        public Texture skinAccessory;

        [Header("BodyShapes")]
        public float Gender;
        public float ChestSize;
        public float FaceShape;
        public float height;
        public float headSize;
        public float shoulderWidth;

        [Header("FaceShapes")]
        public float LashLength;
        public float BrowSize;
        public float EarTipLength;
        [Space]
        public Vector3 EyeSocketPosition;
        public float EyeSocketRotation;
        public Vector3 EyeSocketScale = Vector3.one;
        public float EyeUp;
        public float EyeDown;
        public float EyeSquare;
        [Space]
        public float NoseWidth;
        public float NoseUp;
        public float NoseDown;
        public float NoseBridgeAngle;
        [Space]
        public float MouthWide;
        public float MouthThin;
        [Space]
        public float pupilSize;
        public float irisSize;
        public float outerIrisColorSharpness;
        public float innerIrisColorShapness;
        public Vector2 innerIrisColorOffset;


        public override CharacterData GetCharacterData()
        {
            return UpdateVersion();
        }

        public void SaveCharacter(OutfitSystem outfitSystem)
        {
            Debug.Log("Legacy: Saving this way no longer works this way please use the new system");
        }




        public void LoadCharacter(Transform parent)
        {
            Debug.LogWarning("LoadCharacter is deperciated. Pass GetCharacterData() into Bozo_SaveSystem instead");
        }

        [ContextMenu("Update To Current Version")]
        public CharacterData UpdateVersion()
        {


            var CharacterSave = ScriptableObject.CreateInstance<CharacterObject>();
            CharacterSave.data = new CharacterData();
            CharacterSave.data.outfitDatas = new List<OutfitData>();

            CharacterSave.data.characterName = name;

            //Adding All Outfits
            foreach (var outfit in outfits)
            {
                if (outfit.outfit == null) continue;
                var outfitData = new OutfitData();

                var outfitUpdate = outfit.outfit.GetComponent<Outfit>();

                if (outfitUpdate.Type == null) continue;

                outfitData.outfit = outfitUpdate.Type.name + "/" + outfit.outfit.name;
                outfitData.colors = outfit.colors.ToList();
                outfitData.decal = "";
                outfitData.decalColors = new List<Color>(3);
                outfitData.pattern = "";
                outfitData.patternColors = new List<Color>(3);

                Debug.Log(CharacterSave);
                Debug.Log(outfitData);
                CharacterSave.data.outfitDatas.Add(outfitData);
            }

            //Getting Base Head
            var headData = new OutfitData();
            headData.outfit = "Head/Head_BasicHead";
            headData.colors = SkinColor;
            CharacterSave.data.outfitDatas.Add(headData);
            headData.decal = "";
            headData.decalColors = new List<Color>(3);
            headData.pattern = "";
            headData.patternColors = new List<Color>(3);


            //Getting Base Body
            var bodyData = new OutfitData();
            bodyData.outfit = "Body/Body_BasicBody";
            bodyData.colors = SkinColor;
            CharacterSave.data.outfitDatas.Add(bodyData);
            bodyData.decal = "";
            bodyData.decalColors = new List<Color>(3);
            bodyData.pattern = "";
            bodyData.patternColors = new List<Color>(3);

            //Getting Base Eyes
            var eyeData = new OutfitData();
            eyeData.outfit = "Eyes/Eyes_BasicEyes";
            //eyeData.colors = SkinColor;

            eyeData.decal = "Decal/Decal_BasicPupil";
            eyeData.pattern = "Pattern/Pattern_BasicIris";
            eyeData.decalScale = new Vector4(1,1,0,0);
            eyeData.patternScale = new Vector4(1,1,0,0);

            eyeData.colors = Enumerable.Repeat(EyeColor[3], 9).ToList();
            eyeData.patternColors = new List<Color> { EyeColor[2], EyeColor[1], EyeColor[0] };
            eyeData.decalColors = new List<Color> { EyeColor[2], EyeColor[1], EyeColor[0] };

            //Body Shapes
            CharacterSave.data.bodyIDs = new List<string>();
            CharacterSave.data.bodyIDs.Add("BodyType");
            CharacterSave.data.bodyIDs.Add("Chest");
            CharacterSave.data.bodyIDs.Add("Weight");

            CharacterSave.data.bodyShapes = new List<float>();
            CharacterSave.data.bodyShapes.Add(Gender);
            CharacterSave.data.bodyShapes.Add(ChestSize);
            CharacterSave.data.bodyShapes.Add(0);

            //Face Shapes
            CharacterSave.data.faceIDs = new List<string>();
            CharacterSave.data.faceIDs.Add("BodyType");
            CharacterSave.data.faceIDs.Add("Squareness");
            CharacterSave.data.faceIDs.Add("LashLength");
            CharacterSave.data.faceIDs.Add("BrowThickness");
            CharacterSave.data.faceIDs.Add("NoseBridgeCurve");
            CharacterSave.data.faceIDs.Add("NoseWidth");
            CharacterSave.data.faceIDs.Add("NoseTiltDown");
            CharacterSave.data.faceIDs.Add("NoseTiltUp");
            CharacterSave.data.faceIDs.Add("MouthWide");
            CharacterSave.data.faceIDs.Add("MouthThin");
            CharacterSave.data.faceIDs.Add("EyesOuterCornersLow");
            CharacterSave.data.faceIDs.Add("EyesOuterCornersHigh");
            CharacterSave.data.faceIDs.Add("EyesSquare");
            CharacterSave.data.faceIDs.Add("EarsElf");

            CharacterSave.data.faceShapes = new List<float>();
            CharacterSave.data.faceShapes.Add(Gender);
            CharacterSave.data.faceShapes.Add(FaceShape);
            CharacterSave.data.faceShapes.Add(LashLength);
            CharacterSave.data.faceShapes.Add(BrowSize);
            CharacterSave.data.faceShapes.Add(NoseBridgeAngle);
            CharacterSave.data.faceShapes.Add(NoseWidth);
            CharacterSave.data.faceShapes.Add(NoseDown);
            CharacterSave.data.faceShapes.Add(NoseUp);
            CharacterSave.data.faceShapes.Add(MouthWide);
            CharacterSave.data.faceShapes.Add(MouthThin);
            CharacterSave.data.faceShapes.Add(EyeDown);
            CharacterSave.data.faceShapes.Add(EyeUp);
            CharacterSave.data.faceShapes.Add(EyeSquare);
            CharacterSave.data.faceShapes.Add(EarTipLength);

            //Body Modifications

            CharacterSave.data.bodyModsKeys = new List<string>();
            CharacterSave.data.bodyModsKeys.Add("root");
            CharacterSave.data.bodyModsKeys.Add("head");
            CharacterSave.data.bodyModsKeys.Add("clavicle_l");
            CharacterSave.data.bodyModsKeys.Add("eyeRoot_l");

            CharacterSave.data.bodyMods = new List<BodyModData>();
            var heightMod = new BodyModData();
            heightMod.scaleValue = height + 1;
            var headMod = new BodyModData();
            headMod.scaleValue = headSize + 1;
            var shoulderMod = new BodyModData();
            shoulderMod.scaleValue = shoulderWidth + 1;
            var eyeMod = new BodyModData();
            eyeMod.scale = EyeSocketScale;
            eyeMod.position = EyeSocketPosition;
            eyeMod.rotation = EyeSocketRotation;

            CharacterSave.data.bodyMods.Add(heightMod);
            CharacterSave.data.bodyMods.Add(headMod);
            CharacterSave.data.bodyMods.Add(shoulderMod);
            CharacterSave.data.bodyMods.Add(eyeMod);


            //Saving Asset
            CharacterSave.data.outfitDatas.Add(eyeData);


#if UNITY_EDITOR
            if (!Application.isPlaying) 
            {
                AssetDatabase.CreateAsset(CharacterSave, "Assets/BoZo_StylizedModularCharacters/CustomCharacters/Resources/" + name + ".asset");
                AssetDatabase.ImportAsset("Assets/BoZo_StylizedModularCharacters/CustomCharacters/Resources/" + name + ".asset");
                AssetDatabase.Refresh();
                Debug.Log($"Updated {name} to current version new save located at \"Assets/BoZo_StylizedModularCharacters/CustomCharacters/Resources/\"");
            }
            else
            {
                Debug.LogWarning("Updated Character Save only Saves to disk during Edit Mode");
            }
#endif

            return CharacterSave.data;
        }

        public List<GameObject> GetOutfitsList()
        {
            var list = new List<GameObject>();
            foreach (var item in outfits)
            {
                list.Add(item.outfit);
            }

            return list;
        }

        public Dictionary<OutfitType, GameObject> GetOutfitsDictionary()
        {
            var list = new Dictionary<OutfitType,GameObject>();
            foreach (var item in outfits)
            {
                list.Add(item.type, item.outfit);
            }

            return list;
        }

        [System.Serializable]
        private class OufitParam
        {
            public OutfitType type;
            public GameObject outfit;
            public Color[] colors = new Color[5];
        }
    }
}



