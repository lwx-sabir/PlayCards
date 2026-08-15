#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Fixes the "URP removed N shadow maps to make the others fit in the shadow atlas" overflow. Environment
    /// scenes (the Wardrobe penthouse, the DiveBar world) carry dozens of decorative point/spot lights all set to
    /// cast REALTIME shadows. URP can only fit a handful of additional-light shadow maps in the atlas, so it drops
    /// the rest and shrinks the survivors to a sliver — which is what turns the avatar's cast shadow into a jiggly
    /// blur (and wastes GPU rendering all those shadow maps).
    ///
    /// This disables realtime shadow casting on every POINT/SPOT light in the OPEN scene EXCEPT:
    ///   • DIRECTIONAL lights — the main/key light; its shadow is cheap and always wanted, and
    ///   • any light you currently have SELECTED — the hero caster you want to keep (e.g. the avatar's key spot).
    ///
    /// The lights still ILLUMINATE — only their realtime shadow is turned off, so the lit look is unchanged; you
    /// just stop paying for (and overflowing) dozens of shadow maps. For permanent shadows from those lights, BAKE
    /// them later (baked shadows live in the lightmaps at zero realtime cost) — this is the immediate un-break.
    ///
    /// Idempotent + Undo-able. Run it per scene (Wardrobe, DiveBar, …). Edits inside prefab instances are recorded
    /// as instance overrides so they persist without touching the shared prefab.
    /// </summary>
    public static class LightShadowTrimmer
    {
        [MenuItem("Khela/Lighting/Trim Realtime Shadow Casters (Open Scene)")]
        private static void Trim()
        {
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int turnedOff = 0, keptDirectional = 0, keptSelected = 0;
            var keptNames = new StringBuilder();

            foreach (var l in lights)
            {
                if (l.type == LightType.Directional) { keptDirectional++; continue; }   // main light — leave it
                if (l.shadows == LightShadows.None) continue;                            // already no-shadow

                bool selected = System.Array.IndexOf(Selection.gameObjects, l.gameObject) >= 0;
                if (selected)
                {
                    keptSelected++;
                    keptNames.Append("\n  • ").Append(l.name).Append(" (").Append(l.type).Append(')');
                    continue;   // the hero caster you chose to keep
                }

                Undo.RecordObject(l, "Trim light shadow");
                l.shadows = LightShadows.None;
                EditorUtility.SetDirty(l);
                if (PrefabUtility.IsPartOfPrefabInstance(l))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(l);   // persist the override
                turnedOff++;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[LightTrim] Disabled realtime shadows on {turnedOff} point/spot light(s). " +
                      $"Kept {keptDirectional} directional + {keptSelected} selected caster(s)." +
                      (keptSelected > 0 ? " Kept:" + keptNames : "") +
                      " Lights still illuminate — bake them for permanent shadows at no realtime cost.");
        }
    }
}
#endif
