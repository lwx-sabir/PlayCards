#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Phase A of the 4-tier graphics system (see docs/GRAPHICS_QUALITY.md). Generates the four mobile
    /// tier URP assets (Low/Mid/High/Ultra) + a Mobile_Advanced_Renderer, by DUPLICATING the already-tuned
    /// <c>Mobile_RPAsset</c> and dialing per-tier values through SerializedObject — so Unity owns the GUIDs,
    /// renderer references, and serialization (no hand-authored YAML that could import pink).
    ///
    /// Renderers (both Forward+ — required by the ShadowCatcherAdditional, so we split by FEATURES not mode):
    ///   • Mobile_Renderer          — no SSAO   → Low, Mid
    ///   • Mobile_Advanced_Renderer — + SSAO     → High, Ultra   (a copy of PC_Renderer)
    ///
    /// Deliberately NOT tiered (kept at the working Mobile_RPAsset values so nothing that currently works
    /// breaks): additional-lights rendering mode, additional-light shadows (off — avatar shadow comes from
    /// the MAIN light), depth/normal bias. We tier only the high-impact levers.
    ///
    /// Re-runnable: overwrites the tier assets in place. After running, create Low/Mid/High/Ultra Quality
    /// levels in Project Settings ▸ Quality and assign these four assets.
    /// </summary>
    public static class GenerateMobileQualityTiers
    {
        private const string Dir            = "Assets/Settings";
        private const string BaseUrp        = Dir + "/Mobile_RPAsset.asset";
        private const string BasicRenderer  = Dir + "/Mobile_Renderer.asset";
        private const string PcRenderer     = Dir + "/PC_Renderer.asset";
        private const string AdvRenderer    = Dir + "/Mobile_Advanced_Renderer.asset";

        private struct TierSpec
        {
            public string Name;
            public float RenderScale;
            public int MSAA;            // 1 = off, 2, 4
            public bool HDR;
            public int MainShadowRes;   // 512/1024/2048
            public float ShadowDistance;
            public int Cascades;        // 1..4
            public bool SoftShadows;
            public int SoftQuality;     // 1 Low, 2 Med, 3 High
            public bool DepthTexture;
            public bool Advanced;       // true → Mobile_Advanced_Renderer (SSAO)
        }

        // RenderScale is tiered again (Low 0.7, Mid 0.85 = the low-end GPU lever), BUT it — like MSAA and HDR —
        // only applies cleanly to a FRESH camera. It must NOT be changed on a live camera (URP doesn't resize
        // the render target → blur). So the graphics manager applies the tier on SCENE LOAD; a tier change in a
        // menu takes visible effect the next time a scene loads (players hit that within seconds moving
        // Home→Lobby→Table). See docs/GRAPHICS_QUALITY.md.
        private static readonly TierSpec[] Tiers =
        {
            new TierSpec { Name="Low",   RenderScale=0.70f, MSAA=1, HDR=false, MainShadowRes=1024, ShadowDistance=12, Cascades=1, SoftShadows=false, SoftQuality=2, DepthTexture=false, Advanced=false },
            new TierSpec { Name="Mid",   RenderScale=0.85f, MSAA=2, HDR=false, MainShadowRes=1024, ShadowDistance=20, Cascades=1, SoftShadows=false, SoftQuality=2, DepthTexture=false, Advanced=false },
            new TierSpec { Name="High",  RenderScale=1.00f, MSAA=2, HDR=true,  MainShadowRes=2048, ShadowDistance=40, Cascades=2, SoftShadows=true,  SoftQuality=2, DepthTexture=true,  Advanced=true  },
            new TierSpec { Name="Ultra", RenderScale=1.00f, MSAA=4, HDR=true,  MainShadowRes=2048, ShadowDistance=50, Cascades=4, SoftShadows=true,  SoftQuality=3, DepthTexture=true,  Advanced=true  },
        };

        [MenuItem("Tools/Khela/Graphics/Generate Mobile Quality Tiers")]
        public static void Generate()
        {
            if (AssetDatabase.LoadMainAssetAtPath(BaseUrp) == null)
            {
                EditorUtility.DisplayDialog("Generate Mobile Quality Tiers",
                    $"Base asset not found:\n{BaseUrp}\nAborting.", "OK");
                return;
            }

            // --- 1. Mobile_Advanced_Renderer = copy of PC_Renderer (Forward+ + SSAO + transparent-receive) ---
            if (AssetDatabase.LoadMainAssetAtPath(AdvRenderer) == null)
            {
                if (!AssetDatabase.CopyAsset(PcRenderer, AdvRenderer))
                {
                    Debug.LogError($"[Tiers] Failed to copy {PcRenderer} → {AdvRenderer}.");
                    return;
                }
                var advObj = AssetDatabase.LoadMainAssetAtPath(AdvRenderer);
                advObj.name = "Mobile_Advanced_Renderer";   // fix the internal name to match the file
                EditorUtility.SetDirty(advObj);
            }

            var basicRenderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(BasicRenderer);
            var advancedRenderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(AdvRenderer);
            if (basicRenderer == null || advancedRenderer == null)
            {
                Debug.LogError("[Tiers] Could not load Basic/Advanced renderer data — aborting.");
                return;
            }

            // --- 2. One URP asset per tier ---
            foreach (var t in Tiers)
            {
                string path = $"{Dir}/Mobile_{t.Name}_URP.asset";

                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                    AssetDatabase.DeleteAsset(path);          // re-runnable: replace in place
                if (!AssetDatabase.CopyAsset(BaseUrp, path))
                {
                    Debug.LogError($"[Tiers] Failed to copy base URP → {path}.");
                    continue;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                asset.name = $"Mobile_{t.Name}_URP";
                var so = new SerializedObject(asset);

                SetStr  (so, "m_Name", $"Mobile_{t.Name}_URP");
                SetFloat(so, "m_RenderScale", t.RenderScale);
                SetInt  (so, "m_MSAA", t.MSAA);
                SetInt  (so, "m_SupportsHDR", t.HDR ? 1 : 0);
                SetInt  (so, "m_MainLightShadowmapResolution", t.MainShadowRes);
                SetFloat(so, "m_ShadowDistance", t.ShadowDistance);
                SetInt  (so, "m_ShadowCascadeCount", t.Cascades);
                SetInt  (so, "m_SoftShadowsSupported", t.SoftShadows ? 1 : 0);
                SetInt  (so, "m_SoftShadowQuality", t.SoftQuality);
                SetInt  (so, "m_RequireDepthTexture", t.DepthTexture ? 1 : 0);

                // Renderer reference: element 0 of m_RendererDataList.
                var list = so.FindProperty("m_RendererDataList");
                if (list != null && list.arraySize >= 1)
                    list.GetArrayElementAtIndex(0).objectReferenceValue =
                        t.Advanced ? advancedRenderer : basicRenderer;

                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                Debug.Log($"[Tiers] {path}: scale {t.RenderScale}, MSAA {t.MSAA}x, HDR {t.HDR}, " +
                          $"shadow {t.MainShadowRes}/{t.ShadowDistance}m/{t.Cascades}casc soft={t.SoftShadows}, " +
                          $"renderer={(t.Advanced ? "Advanced+SSAO" : "Basic")}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Tiers] Done. Next: Project Settings ▸ Quality → create Low/Mid/High/Ultra levels and " +
                      "assign Mobile_Low/Mid/High/Ultra_URP; keep the PC level. Set Android/iOS default = Mid.");
        }

        private static void SetInt(SerializedObject so, string prop, int v)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.intValue = v; else Debug.LogWarning($"[Tiers] missing prop {prop}");
        }
        private static void SetFloat(SerializedObject so, string prop, float v)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.floatValue = v; else Debug.LogWarning($"[Tiers] missing prop {prop}");
        }
        private static void SetStr(SerializedObject so, string prop, string v)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.stringValue = v;
        }
    }
}
#endif
