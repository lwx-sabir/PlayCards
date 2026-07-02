using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using System.Threading.Tasks;
using System;
using System.IO;


namespace Bozo.ModularCharacters
{
    public static class BMAC_SaveSystem
    {
        public static string filePath = Application.persistentDataPath + "/BoZo_StylizedModularCharacters/CustomCharacters";
        public static string iconFilePath = Application.persistentDataPath + "/BoZo_StylizedModularCharacters/CustomCharacters/Icons";

        public static void SaveCharacter(OutfitSystem outfitSystem, string saveName, Texture2D icon = null) 
        {
            var data = GetCharacterData(outfitSystem);

            data.characterName = saveName;


#if UNITY_EDITOR
            var assetPath = CharacterToolSettingsProvider.Get().saveDataFolder;
            var CharacterSave = ScriptableObject.CreateInstance<CharacterObject>();
            CharacterSave.data = data;
            CharacterSave.icon = icon;
            AssetDatabase.CreateAsset(CharacterSave, assetPath + "/" + saveName + ".asset");
            AssetDatabase.Refresh();
    #endif

            string saveData = JsonUtility.ToJson(data);
            System.IO.File.WriteAllText(filePath + "/" + saveName + ".json", saveData);
            Debug.Log("Character Saved:" + filePath + "/" + saveName + ".json");
        }

        public static CharacterData GetCharacterData(OutfitSystem outfitSystem)
        {

            var data = new CharacterData();

            data.versionID = 1;

            //Saving BlendShapes
            var bodyShapeValues = outfitSystem.GetBodyShapeValues();
            data.bodyIDs = bodyShapeValues.Keys.ToList();
            data.bodyShapes = bodyShapeValues.Values.ToList();

            var faceShapeValues = outfitSystem.GetFaceShapeValues();
            data.faceIDs = faceShapeValues.Keys.ToList();
            data.faceShapes = faceShapeValues.Values.ToList();

            //Saving Body Mods
            var modData = new List<BodyModData>();
            var modKeys = outfitSystem.bodyModifiers.Keys.ToList();
            for (int i = 0; i < modKeys.Count; i++)
            {
                var mod = outfitSystem.bodyModifiers[modKeys[i]].GetData();
                modData.Add(mod);
            }
            data.bodyMods = modData;
            data.bodyModsKeys = modKeys;

            //Saving Outfits
            var outfits = outfitSystem.GetOutfits();
            var outfitDataList = new List<OutfitData>();

            for (int i = 0; i < outfits.Count; i++)
            {
                if (outfits[i] == null) continue;
                outfitDataList.Add(outfits[i].GetOutfitData());
            }

            data.stance = outfitSystem.stance;
            data.outfitDatas = outfitDataList;

            return data;
        }

        public static async Task LoadCharacter(OutfitSystem outfitSystem, CharacterData characterObject, bool manualShapeApply = false, bool async = false)
        {
            if (outfitSystem.isloading)
            {
                Debug.LogWarning($"{outfitSystem.name} is already trying to load a save. Please wait until its finished before loading another one");
                return;
            }

            outfitSystem.isloading = true;
            outfitSystem.GetCharacterBody().enabled = false;
            CharacterData loadData = Bozo_SavePatcher.UpdateSave(characterObject);

            //Loading Outfits

            List<Outfit> outfits = new List<Outfit>();

            foreach (var item in loadData.outfitDatas)
            {
                if (outfitSystem.async)
                {
                    var request = Resources.LoadAsync<Outfit>(item.outfit);

                    while (!request.isDone)
                    {
                        await Task.Yield();
                    }

                    outfits.Add(request.asset as Outfit);
                }
                else
                {
                    outfits.Add(Resources.Load<Outfit>(item.outfit));
                }
            }

            outfitSystem.RemoveAllOutfits();

            for (int i = 0; i < loadData.outfitDatas.Count; i++)
            {
                try
                {
                    var outfitData = loadData.outfitDatas[i];
                    var outfit = outfits[i];

                    if (i <= 0 && i > outfits.Count)
                    {
                        continue;
                    }

                    if (outfit == null)
                    {
                        Debug.LogWarning("Outfit Path: " + outfitData.outfit + " returns null make sure Prefab is named correctly");
                        continue;
                    }

                    var inst = UnityEngine.Object.Instantiate(outfit, outfitSystem.transform);
                    inst.Attach(outfitSystem);

                    if(loadData.outfitDatas[i].decalDatas != null)
                    {
                        inst.decalDatas = new(loadData.outfitDatas[i].decalDatas);
                    }

                    try
                    {
                        for (int c = 0; c < 9; c++)
                        {
                            if (outfitData.colors.Count > c)
                            {
                                inst.SetColor(outfitData.colors[c], c + 1);
                            }

                            if (c + 1 <= 3 && outfitData.pattern != "" && outfitData.decalColors != null)
                            {
                                inst.SetPatternColor(outfitData.patternColors[c], c + 1);
                            }
                        }
                    }
                    catch
                    {
                        Debug.LogWarning(loadData.outfitDatas[i].outfit + " Colors has failed loading");
                    }

                    var pattern = Resources.Load<Texture>(outfitData.pattern);
                    inst.SetPattern(pattern);
                    inst.SetPatternSize(outfitData.patternScale);

                    try
                    {
                        if (loadData.outfitDatas[i].partVisibility != null)
                        {
                            for (int v = 0; v < loadData.outfitDatas[i].partVisibility.Length; v++)
                            {
                                if (inst.optionalPieces[v] == null) continue;
                                inst.optionalPieces[v].SetActive(loadData.outfitDatas[i].partVisibility[v]);
                            }
                        }
                    }
                    catch
                    {
                        Debug.LogError($"{inst}Something Went Wrong");
                    }
                }
                catch
                {
                    Debug.LogWarning(loadData.outfitDatas[i].outfit +" Has failed loading");
                }


                if(outfitSystem.async) await Task.Yield();
            }


            //Loading Body Morphs



            outfitSystem.isloading = false;

            await outfitSystem.MergeCharacter();
            outfitSystem.GetCharacterBody().enabled = true;
            outfitSystem.ResetShapes();

            for (int i = 0; i < loadData.bodyIDs.Count; i++)
            {
                outfitSystem.SetShape(loadData.bodyIDs[i], loadData.bodyShapes[i]);
            }

            for (int i = 0; i < loadData.faceIDs.Count; i++)
            {
                outfitSystem.SetShape(loadData.faceIDs[i], loadData.faceShapes[i]);
            }

            LoadBodyMods(outfitSystem, loadData);
            outfitSystem.SetStance(loadData.stance);
            outfitSystem.OnCharacterLoaded?.Invoke();
        }

        public static void LoadBodyMods(OutfitSystem outfitSystem, CharacterData loadData)
        {
            for (int i = 0; i < loadData.bodyModsKeys.Count; i++)
            {
                outfitSystem.bodyModifiers[loadData.bodyModsKeys[i]].SetData(loadData.bodyMods[i]);
            }
        }

        public static CharacterData GetDataFromID(string saveName)
        {
            CharacterData loadData;
            Debug.Log("Attempted Load at: " + filePath + "/" + saveName + ".json");
            if (!System.IO.File.Exists(filePath + "/" + saveName + ".json"))
            {
                Debug.LogWarning($"Save ID: {saveName} does not exist. Make sure input matches an existing Save");
                return null;
            }
            else
            {
                string data = System.IO.File.ReadAllText(filePath + "/" + saveName + ".json");
                loadData = JsonUtility.FromJson<CharacterData>(data);
                return loadData;
            }
        }

        public static async Task<List<Outfit>> LoadOutfits(List<OutfitData> outfitDatas)
        {
            var loadTasks = outfitDatas.Select(data => LoadResourceAsync<Outfit>(data.outfit));
            return (await Task.WhenAll(loadTasks)).ToList();
        }

        public static async Task<T> LoadResourceAsync<T>(string path) where T : UnityEngine.Object
        {
            ResourceRequest request = Resources.LoadAsync<T>(path);
            var tcs = new TaskCompletionSource<T>();

            request.completed += operation =>
            {
                if (request.asset == null)
                {
                    tcs.SetResult(null);
                }
                else if (request.asset is not T result)
                {
                    tcs.SetResult(null);
                }
                else
                {
                    tcs.SetResult(result);
                }
            };

            return await tcs.Task;
        }

        public static void DeleteCharacter(string characterName)
        {
            System.IO.File.Delete(filePath + "/" + characterName + ".json");
            System.IO.File.Delete(iconFilePath + "/" + characterName + ".png");
#if UNITY_EDITOR
            var assetPath = CharacterToolSettingsProvider.Get().saveDataFolder;
            var iconAssetPath = CharacterToolSettingsProvider.Get().iconFolder;
            AssetDatabase.DeleteAsset(assetPath + "/" + characterName + ".asset");
            AssetDatabase.DeleteAsset(assetPath + "/" + characterName + ".meta");
            AssetDatabase.DeleteAsset(iconAssetPath + "/" + characterName + ".png");
            AssetDatabase.DeleteAsset(iconAssetPath + "/" + characterName + ".meta");
            AssetDatabase.Refresh();
    #endif
        }
    }

    [System.Serializable]
    public class CharacterData
    {
        public int versionID = 0;
        public string characterName;
        public List<string> bodyIDs;
        public List<float> bodyShapes;
        public List<string> faceIDs;
        public List<float> faceShapes;
        public List<string> bodyModsKeys;
        public List<BodyModData> bodyMods;
        public List<OutfitData> outfitDatas;
        public OutfitData bodyData;
        public float stance;


        public CharacterData()
        {
            versionID = 0;
        }
        public CharacterData(CharacterData copyData)
        {
            versionID = copyData.versionID;
            characterName = copyData.characterName;
            bodyIDs = new List<string>(copyData.bodyIDs);
            bodyShapes = new List<float>(copyData.bodyShapes);
            faceIDs = new List<string>(copyData.faceIDs);
            faceShapes = new List<float>(copyData.faceShapes);
            bodyModsKeys = new List<string>(copyData.bodyModsKeys);
            bodyMods = new List<BodyModData>(copyData.bodyMods);
            outfitDatas = new List<OutfitData>(copyData.outfitDatas);
            bodyData = copyData.bodyData;
            stance = copyData.stance;
        }
    }

    [System.Serializable]
    public struct OutfitData
    {
        public string outfit;
        public List<Color> colors;

        public string decal;
        public List<Color> decalColors;
        public Vector4 decalScale;

        public string pattern;
        public List<Color> patternColors;
        public Vector4 patternScale;

        public bool[] partVisibility;

        //Custom Shader Data
        public Color color;
        public int swatch;

        public List<DecalData> decalDatas;
    }

    [System.Serializable]
    public struct DecalData
    {
        public string decal;
        public Texture decalTexture;
        public string parent;

        public Vector3 pos;

        public float yaw;
        public float roll;
        public float pitch;
        public float scale;

        public Color color;

        public DecalData(string decal, string parent, Vector3 pos, float yaw, float roll, float pitch, float scale, Color color)
        {
            this.decal = decal;
            this.decalTexture = null;
            this.parent = parent;

            this.pos = pos;

            this.yaw = yaw;
            this.roll = roll;
            this.pitch = pitch;
            this.scale = scale;

            this.color = color;
        }
    }

}