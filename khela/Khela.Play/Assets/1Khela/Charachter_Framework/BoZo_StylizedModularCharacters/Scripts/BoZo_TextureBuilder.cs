using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Bozo.ModularCharacters
{
    public static class BoZo_TextureBuilder
    {
        private static BoZo_TextureBuilderManager _manager;


        public static void Init()
        {
            var ob = new GameObject("BoZo_TextureBuilderManager");
            ob.AddComponent(typeof(BoZo_TextureBuilderManager));
            _manager = ob.GetComponent<BoZo_TextureBuilderManager>();
        }

        public static void AddOutfit(OutfitSystem system)
        {
            if (_manager == null) Init();
            _manager.AddOutfit(system);
        }

        public static void RemoveBuilder(OutfitSystem system)
        {
            if (_manager == null) return;
            _manager.RemoveBuilder(system);
        }

        public static List<Material> GetMaterials(OutfitSystem system)
        {
            return _manager.GetMaterials(system);
        }

        public static void QuickBuild(OutfitSystem system, bool save = false)
        {
            if (_manager == null) Init();
            _manager.QuickBuild(system, save);
        }

        public static DecalBaker GetDecalControl(OutfitSystem outfitSystem, Outfit outfit)
        {
            if (_manager == null) return null;
            return _manager.GetDecalControl(outfitSystem, outfit);
        }

        public class BoZo_TextureBuilderManager : MonoBehaviour
        {
            TextureBuilder commonBuilder;

            Dictionary<OutfitSystem, TextureBuilder> builders = new Dictionary<OutfitSystem, TextureBuilder>();
            public List<TextureBuilder> visableBuilders = new List<TextureBuilder>();

            private static HashSet<OutfitSystem> buildQueue = new HashSet<OutfitSystem>();
            private bool isBuilding;

            public async void QuickBuild(OutfitSystem system, bool save = false)
            {
                if (system == null) return;
                if (isBuilding)
                {
                    buildQueue.Add(system);
                    return;
                }

                isBuilding = true;

                if (commonBuilder == null)
                {
                    commonBuilder = new TextureBuilder();
                    commonBuilder.parent.position = new Vector3(-12, -100, 0);

                }
                commonBuilder.locked = false;

                if (commonBuilder.outfitSystem == null)
                {
                    var characterBaseOb = Resources.Load<OutfitSystem>("BSMC_CharacterMergedBase");
                    var body = Instantiate(characterBaseOb, new Vector3(0, -100, 0), Quaternion.identity);
                    body.name = "CommonBuilder_MergedBase";
                    //body.mergeBase = true;
                    body.MuteHeightChange(true);
                    body.animator.enabled = false;
                    body.mergeMaterial = system.mergeMaterial;
                    commonBuilder.Link(body);
                }

               // var height = system.height;
                var data = system.data;
                BMAC_SaveSystem.LoadCharacter(commonBuilder.outfitSystem, data, true, true);
                commonBuilder.outfitSystem.data = data;


                await Task.Yield();

                commonBuilder.locked = true;
                system.customMaps = commonBuilder.outfitSystem.customMaps;

                var optimizer = new BoZo_CharacterOptimizer();
                commonBuilder.outfitSystem.prefabName = system.prefabName;
                var mergedBody = await optimizer.OptimizeCharacter(commonBuilder.outfitSystem, data, save);
                system.SetCharacterBody(mergedBody);
                //system.SetHeight(height);

                system.SetRenderTextures(commonBuilder.rts.Values.ToList());

                Destroy(commonBuilder.outfitSystem.gameObject);

                await Task.Yield();

                commonBuilder.Remove();
                commonBuilder = null;

                isBuilding = false;
                if (buildQueue.Count != 0)
                {
                    var next = buildQueue.First();
                    buildQueue.Remove(next);
                    QuickBuild(next);
                }
            }

            public void AddOutfit(OutfitSystem system)
            {
                if (builders.ContainsKey(system)) return;
                var builder = new TextureBuilder();
                builder.Link(system);
                builders.Add(system, builder);
                visableBuilders.Add(builder);

                var list = builders.Values.ToList();
                for (int i = 0; i < list.Count; i++) { list[i].Organize(i); }
            }

            public void RemoveBuilder(OutfitSystem system)
            {
                if (!builders.ContainsKey(system)) return;

                var builder = builders[system];

                builder.Remove();

                builders.Remove(system);
            }

            public List<Material> GetMaterials(OutfitSystem system)
            {
                if (builders.ContainsKey(system)) return builders[system].GetMaterials();
                return commonBuilder.GetMaterials();
            }

            public DecalBaker GetDecalControl(OutfitSystem outfitSystem, Outfit outfit)
            {
                if (builders.ContainsKey(outfitSystem))
                {
                    if (builders[outfitSystem].decalOutfit.ContainsKey(outfit.Type.name)) return builders[outfitSystem].decalOutfit[outfit.Type.name];
                };
                if (commonBuilder != null) return commonBuilder.decalOutfit[outfit.Type.name];
                return null;
            }

            [System.Serializable]
            public class TextureBuilder
            {
                public Transform parent;

                public OutfitSystem outfitSystem;
                public OutfitSystem proxySystem;

                public Dictionary<string, Material> masterMaterials = new Dictionary<string, Material>();
                private Dictionary<string, Material> unpackedMaterials = new Dictionary<string, Material>();

                private Dictionary<string, Outfit> outfits = new Dictionary<string, Outfit>();
                private Dictionary<string, Dictionary<string, Material>> materials = new Dictionary<string, Dictionary<string, Material>>();


                public Dictionary<string, DecalBaker> decalOutfit = new Dictionary<string, DecalBaker>();

                public Camera cam;
                public List<string> properties = new List<string>();
                public HashSet<string> allProperties = new HashSet<string>();
                public HashSet<string> SubIDs = new HashSet<string>();
                public Dictionary<string, Color> baseColors = new Dictionary<string, Color>();
                public Dictionary<string, Renderer> meshes = new Dictionary<string, Renderer>();
                public Dictionary<string, RenderTexture> rts = new Dictionary<string, RenderTexture>();

                private bool renderCooldown;
                public bool locked; //prevent textures from updating
                public HashSet<string> renderQueue = new HashSet<string>();



                public TextureBuilder()
                {
                    parent = new GameObject().transform;
                    parent.parent = _manager.transform;

                    //Camera Settings
                    GameObject camObject = new GameObject("Builder Camera");
                    camObject.transform.parent = parent;
                    camObject.transform.position = new Vector3(0, 0, 0);
                    cam = camObject.AddComponent<Camera>();

                    cam.orthographic = true;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = Color.clear;
                    cam.orthographicSize = 0.5f;
                    cam.farClipPlane = 3;
                    cam.allowHDR = false;
                    cam.enabled = false;

                    //Proxy Character Settings
                    var characterBaseOb = Resources.Load<OutfitSystem>("BSMC_CharacterMergedBase");
                    proxySystem = Instantiate(characterBaseOb);
                    //proxySystem.mergeBase = true;
                    proxySystem.name = "Proxy System";
                    proxySystem.transform.parent = parent;
                    proxySystem.transform.position = new Vector3(0, -0.5f, 0);
                    proxySystem.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);

                }

                public void Link(OutfitSystem sys)
                {
                    parent.name = sys.name;
                    outfitSystem = sys;

                    foreach (var item in outfitSystem.GetOutfits())
                    {
                        AddOutfit(item);
                    }

                    sys.OnOutfitChanged += AddOutfit;
                    sys.OnOutfitRemoved += RemoveOutfit;
                    sys.OnCharacterLoaded += PrepareRender;
                }

                public void AddOutfit(Outfit outfit)
                {
                    if (outfit == null) return;
                    if (outfitSystem == null) return;
                    outfits[outfit.Type.name] = outfit;


                    string unpackedID = outfit.materialIndex + outfit.Type.name;
                    string packedID = outfit.materialIndex.ToString();
                    //Unpacked Outfit Making Extra Set Up
                    if (outfit.materialIndex == -1 && !meshes.ContainsKey(unpackedID))
                    {
                        InitalizeWorkSpace(outfit.materialIndex, outfit.Type.name);
                    }
                    else if (!meshes.ContainsKey(outfit.materialIndex + "MainTexture"))
                    {
                        InitalizeWorkSpace(outfit.materialIndex);
                    }

                    //Adding Decal System ----------------------------------------------------------------------------------------------
                    if (outfit.supportDecals)
                    {
                        var o = Resources.Load<Outfit>(outfit.GetOutfitData().outfit); 
                        var outfitCopy =  Instantiate(o, proxySystem.transform);
                        outfitCopy.Attach(true);
                        outfitCopy.gameObject.SetActive(true);

                        outfitCopy.gameObject.AddComponent<DecalBaker>();
                        var decalBaker = outfitCopy.GetComponent<DecalBaker>();
                        decalBaker.bones = proxySystem.GetBones();
                        //decalBaker.Init(outfit, outfitCopy, outfitSystem);


                        decalOutfit[outfitCopy.Type.name] = decalBaker;
                        decalBaker.onDecalChanged += PrepareRender;


                        if (outfit.materialIndex == -1)
                        {
                            decalBaker.SetBoundsPosition(meshes[unpackedID + "MainTexture"].transform.localPosition);
                        }
                        else
                        {
                            decalBaker.SetBoundsPosition(meshes[packedID + "MainTexture"].transform.localPosition);
                        }
                    }
                    //----------------------------------------------------------------------------------------------

                    if (outfit.gameObject.activeSelf == false) return;

                    Material editMat = null;


                    foreach (var key in properties)
                    {
                        var fullKey = outfit.materialIndex + key;
                        if (outfit.materialIndex == -1) fullKey = outfit.materialIndex + outfit.Type.name + key;

                        if (outfit.editMaterials.ContainsKey(fullKey))
                        {
                            editMat = outfit.editMaterials[fullKey];
                        }
                        else
                        {
                            editMat = new Material(Shader.Find("BoZo/BakeTexture"));
                            editMat.enableInstancing = true;

                            if (outfit.material.GetTexture("_IDMap") != null)
                            {
                                editMat.SetTexture("_IDMap", outfit.material.GetTexture("_IDMap"));
                                editMat.SetFloat("_UsingIDMap", 1);
                            }

                            editMat.name = outfit.OutfitName;

                            if (key == "MainTexture")
                            {
                                editMat.mainTexture = outfit.material.mainTexture;
                                var colors = outfit.GetColors();

                                if (outfit.material.HasProperty("_UseCustomTexture"))
                                {
                                    if (outfit.material.GetFloat("_UseCustomTexture") == 1)
                                    {
                                        for (int i = 1; i < 10; i++)
                                        {
                                            if (editMat.HasProperty("_Color_" + i))
                                            {
                                                editMat.SetColor("_Color_" + i, colors[i - 1]);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        editMat.SetFloat("_UseCustomTexture", 0);
                                        editMat.color = outfit.GetColor(1);
                                    }
                                }
                            }
                            else if (!key.Contains("_"))
                            {
                                //Special Texture
                                foreach (var ext in outfit.GetComponentsInChildren<IOutfitExtension>())
                                {
                                    if (ext.GetID() == key)
                                    {
                                        editMat.mainTexture = (Texture)ext.GetValue();
                                        if (meshes[fullKey].sharedMaterials.Length == 0)
                                        {
                                            outfitSystem.customMaps.Remove(key);
                                        }
                                        else
                                        {
                                            outfitSystem.customMaps[key] = rts[fullKey];
                                        }
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                if (outfit.material.HasProperty(key))
                                {
                                    editMat.mainTexture = outfit.material.GetTexture(key);
                                    editMat.SetFloat("_UseCustomTexture", 0);

                                }
                                var ID = fullKey.Replace(key, "MainTexture");
                                editMat.SetTexture("_IDMap", materials[ID][outfit.Type.name].mainTexture);
                                editMat.SetFloat("_UsingIDMap", 1);
                                if (key.Contains("Normal") || key.Contains("normal")) editMat.SetFloat("_isNormalMap", 1);
                            }
                        }

                        editMat.SetColor("_BaseColor", baseColors[key]);
                        if (editMat.mainTexture == null)
                        {
                            editMat.SetFloat("_hasTextureMap", 0);
                        }

                        materials[fullKey][outfit.Type.name] = editMat;
                        ReassignMaterials();

                        outfit.editMaterials[fullKey] = editMat;
                        Render(fullKey, true);


                    }


                    Material mat;
                    var list = masterMaterials;
                    if (outfit.materialIndex == -1) list = unpackedMaterials;

                    if (list.ContainsKey(outfit.materialIndex + outfit.Type.name))
                    {
                        mat = list[outfit.materialIndex + outfit.Type.name];
                    }
                    else
                    {
                        mat = list[outfit.materialIndex.ToString()];
                    }

                    outfit.SetMaterial(mat);


                    //outfit.editMode = true;
                    outfit.OnColorChanged += PrepareRender;
                }

                public void InitalizeWorkSpace(int materialIndex = 0, string outfitID = "")
                {
                    var subID = materialIndex + outfitID;

                    if (allProperties.Contains(subID + "MainTexture")) return;
                    var sys = outfitSystem;

                    SubIDs.Add(subID);

                    allProperties.Add(subID + "MainTexture");
                    if (!properties.Contains("MainTexture")) properties.Add("MainTexture");
                    AddToWorkSpace(materialIndex, "MainTexture", outfitID, Color.black);

                    for (int i = 0; i < sys.materialData.Length; i++)
                    {
                        var prop = sys.materialData[i].toMateiralProperty;

                        if (!allProperties.Contains(subID + prop)) allProperties.Add(subID + prop);
                        if (!properties.Contains(prop)) properties.Add(prop);
                        AddToWorkSpace(materialIndex, prop, outfitID, sys.materialData[i].backgroundColor);
                    }
                }

                public void AddToWorkSpace(int materialIndex, string propertyID, string outfitID, Color color)
                {
                    var index = materialIndex;
                    if (materialIndex == -1) index = 12 + unpackedMaterials.Count;
                    var z = (index + 1) * 3;

                    var y = properties.IndexOf(propertyID) - 1;

                    var subID = materialIndex + outfitID + propertyID;


                    Material masterMat;
                    var list = masterMaterials; // Good boy list
                    if (subID.Contains("-1")) list = unpackedMaterials; //Naughty List

                    var MatID = materialIndex.ToString();
                    if (materialIndex == -1) MatID = MatID + outfitID;

                    //Good Boy List (PrePacked Outfits)
                    if (list.ContainsKey(MatID))
                    {
                        masterMat = list[MatID];
                    }
                    else
                    {
                        masterMat = new Material(outfitSystem.mergeMaterial);
                        masterMat.name = MatID;
                        list[MatID] = masterMat;
                    }

                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.transform.parent = parent;
                    quad.transform.localPosition = new Vector3(0, -2 * (y + 1), z);
                    quad.name = subID + "_Quad";
                    meshes[subID] = quad.GetComponent<Renderer>();


                    RenderTexture rt = new RenderTexture(2048, 2048, 0, GraphicsFormat.R8G8B8A8_SRGB);
                    rt.wrapMode = TextureWrapMode.Repeat;
                    rt.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.D16_UNorm;
                    rt.enableRandomWrite = false;
                    rt.useMipMap = false;
                    rt.Create();

                    rts[subID] = rt;

                    materials[subID] = new Dictionary<string, Material>();
                    if (propertyID == "MainTexture") { masterMat.mainTexture = rt; }
                    else if (masterMat.HasProperty(propertyID)) { masterMat.SetTexture(propertyID, rt); }

                    baseColors[propertyID] = color;
                }

                public void RemoveOutfit(Outfit outfit)
                {
                    if (!outfits.ContainsValue(outfit)) { return; };

                    outfits[outfit.Type.name].OnColorChanged -= PrepareRender;

                    outfits.Remove(outfit.Type.name);

                    ReassignMaterials();


                    foreach (var id in meshes.Keys) { Render(id); }
                }

                public void ReassignMaterials()
                {
                    var outfitList = new List<Outfit>(outfits.Values.ToList());
                    outfitList.Sort((a, b) => b.materialPriority.CompareTo(a.materialPriority));

                    foreach (var key in allProperties)
                    {
                        var matList = new List<Material>();
                        foreach (var o in outfitList)
                        {
                            var fullKey = key;
                            if (!meshes.ContainsKey(fullKey)) return;
                            if (!meshes[fullKey]) return;


                            if (materials[fullKey].ContainsKey(o.Type.name))
                            {
                                matList.Add(materials[fullKey][o.Type.name]);
                                meshes[fullKey].sharedMaterials = matList.ToArray();
                                if (key.Contains("MainTexture")) o.SetControlMaterial(meshes[fullKey], matList.Count - 1);
                                o.UpdateMaterialBlock();
                            }
                            for (int i = 0; i < meshes[fullKey].sharedMaterials.Length; i++)
                            {
                                meshes[fullKey].sharedMaterials[i].renderQueue = 1000 + i;
                            }
                        }
                    }
                }

                public void Remove()
                {
                    locked = true;
                    Clean();
                    cam.targetTexture = null;
                    Destroy(parent.gameObject);
                    Destroy(proxySystem.gameObject);
                    foreach (var item in meshes.Values)
                    {
                        Destroy(item.gameObject);
                    }
                }

                public void Clean()
                {
                    masterMaterials.Clear();
                    unpackedMaterials.Clear();

                    foreach (var key in materials.Keys)
                    {
                        foreach (var value in materials[key].Values)
                        {
                            Destroy(value);
                        }
                    }
                    materials.Clear();

                    properties.Clear();
                    allProperties.Clear();
                    SubIDs.Clear();
                    rts.Clear(); // Don't release them since objects still use them

                    foreach (var mesh in meshes.Values)
                    {
                        Destroy(mesh);
                    }
                    meshes.Clear();

                    //if (outfitSystem) Destroy(outfitSystem.gameObject);
                    outfitSystem = null;
                }

                public void Organize(int x)
                {
                    parent.position = new Vector3(10 * x, -5000, 0);
                    return;
                    var index = 0;
                }

                public void PrepareRender(Outfit outfit)
                {
                    var id = outfit.materialIndex + outfit.Type.name + "MainTexture";
                    if (rts.ContainsKey(id))
                    {
                        Render(id);
                    }
                    else
                    {
                        id = id.Replace(outfit.Type.name, "");
                        Render(id);
                    }
                }

                public void PrepareRender()
                {
                    Render();
                }

                public async void Render(string id = "", bool force = false)
                {
                    if (!cam) return;
                    if (renderCooldown == true)
                    {
                        if (id != "") renderQueue.Add(id);
                        return;
                    }


                    if (locked) return;


                    renderCooldown = true;
                    if (rts.ContainsKey(id))
                    {
                        var activeRT = RenderTexture.active;
                        RenderTexture.active = rts[id];
                        GL.Clear(true, true, Color.clear);
                        RenderTexture.active = activeRT;

                        cam.targetTexture = rts[id];
                        var meshPos = meshes[id].transform.position;
                        cam.transform.position = new Vector3(meshPos.x, meshPos.y, meshPos.z - 1);
                        cam.Render();
                    }

                    renderCooldown = false;

                    if (renderQueue.Count != 0)
                    {
                        id = renderQueue.First();
                        renderQueue.Remove(id);
                        Render(id);
                    }
                }

                
                public List<Material> GetMaterials()
                {
                    var list = new List<Material>();
                    list.AddRange(masterMaterials.Values.ToList());
                    list.AddRange(unpackedMaterials.Values.ToList());

                    foreach (var item in list)
                    {
                        item.name = item.name.Replace("-1", "");
                    }

                    return list;
                }
            }
        }

        struct DecalPositions
        {
            public Vector3 pos;
            public Vector3 rot;
            public Vector3 scale;
        }
    }
}


