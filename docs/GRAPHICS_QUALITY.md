# Graphics Quality Tiers — Low / Medium / High / Ultra

Plan for a 4-tier mobile graphics-quality system with **automatic device detection** + **manual
selection from Settings**. Unity 6000.3 / URP 17. Reconciled with what we've already built
(PostFxTierController, the Forward+ shadow-catcher, SceneFrameRate, the baked world lighting).

Research-verified against Unity 6 docs (deep-research 2026-07-27); citations inline.

---

## 1. Goal & tiers

One `GraphicsTier { Low, Mid, High, Ultra }` drives **everything**: the URP quality level (shadows /
render scale / MSAA / lights), the post-processing profile, the avatar shadow method, and the FPS
ceiling. Auto-picked on first run from device capability; overridable in Settings; persisted.

Default on Android = **Medium**.

---

## 2. Architecture (verified)

- **One URP RenderPipelineAsset per Quality level** is the intended Unity model. `QualitySettings.
  SetQualityLevel(index, applyExpensiveChanges=true)` swaps **both** the quality preset **and** that
  level's assigned URP asset in one call — no separate pipeline assignment needed.
  ([Unity 6 docs](https://docs.unity3d.com/6000.1/Documentation/Manual/urp/quality/quality-settings-through-code.html))
- Pipeline resolution is a two-level hierarchy: `GraphicsSettings.defaultRenderPipeline` (project
  default) + `QualitySettings.renderPipeline` (per-level override, wins). If a level has no asset it
  falls back to the default. ([Unity docs](https://docs.unity3d.com/6000.1/Documentation/Manual/srp-setting-render-pipeline-asset.html))
- To fine-tune the *active* asset at runtime (e.g. nudge render scale for one weird device), cast
  `GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset` (null-check) and set the
  property. This **complements** level switching — use it only for device-specific nudges, not as the
  main mechanism. ([Unity docs](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/quality/change-urp-asset-settings.html))
- Both `SetQualityLevel` and direct `QualitySettings.renderPipeline` assignment are valid (the claim
  that only `SetQualityLevel` is "correct" was **refuted** 0-3). We use `SetQualityLevel` as the clean
  primary path.

**Render scale is the #1 low-end lever:** render the 3D view at a lower resolution and upscale, while
the UI stays at native res. URP 17 upscaling filters: Bilinear, Nearest, **FSR 1**, STP.

---

## 3. Three corrections to the generic "duplicate MobileUrp ×4" plan

The pasted plan is a good skeleton but conflicts with what this project already needs:

1. **All 4 renderers stay Forward+ — do NOT split by rendering mode.** The generic plan puts Low/Mid on
   a plain-Forward "Basic" renderer. But our `Khela/ShadowCatcherAdditional` (the spot-light body
   shadow under avatars) **requires Forward+** (`_CLUSTER_LIGHT_LOOP`) — plain Forward kills it (the
   exact bug fixed on 2026-07-26, see [[avatar-stage-shadow]]). So the renderer split is by
   **renderer *features* (SSAO on/off)**, not rendering mode. Two renderers, both Forward+:
   - `Mobile_Basic_Renderer` — Forward+, **no SSAO**, `ShadowTransparentReceive` on. → Low, Mid
   - `Mobile_Advanced_Renderer` — Forward+, **SSAO** on, `ShadowTransparentReceive` on. → High, Ultra
2. **`Application.targetFrameRate` has one owner.** The generic `GraphicsQualityManager` sets fps per
   tier, but we already have per-scene `SceneFrameRate` (Home 30 / Table 60) that runs on scene load
   and would race it. Rule: **the tier sets a ceiling (Low/Mid=30, High/Ultra=60); `SceneFrameRate`
   clamps to `min(scenePref, tierCeiling)`.** A menu scene asking for 30 stays 30 on every tier; a
   60-fps table is capped to 30 on Low/Mid devices. One writer (`SceneFrameRate`), tier-aware.
3. **We already have a 3-tier post system — extend it, don't rebuild.** `PostFxTierController` +
   `PP_Base` + `PP_{Low,Mid,High}_Override` exist ([[table-postfx-tiers]]). Add `PP_Ultra_Override`
   and make the controller *read the tier from GraphicsQualityManager* instead of auto-detecting on
   its own (today it has its own `AutoDetect`). Single source of truth for the tier.

---

## 4. Per-tier settings (reconciled with our shadow config + baked lighting)

| Setting | Low | Mid | High | Ultra |
|---|---|---|---|---|
| **Render Scale** | 1.0 | 1.0 | 1.0 | 1.0 | ⚠️ deferred — see note |
| MSAA | Off | 2× | 2× | 4× |
| HDR | Off | Off | On | On |
| Rendering mode | Forward+ | Forward+ | Forward+ | Forward+ |
| Renderer | Basic (no SSAO) | Basic | Advanced (SSAO) | Advanced (SSAO) |
| Main-light shadows | 1024 | 1024 | 2048 | 2048 |
| Shadow distance | 12 | 20 | 40 | 50 |
| Cascades | 1 | 1 | 2 | 4 |
| Soft shadows | Off | Off | Med | High |
| Depth-/Normal-bias | 0.1 / 0.5 | 0.1 / 0.5 | 0.1 / 0.5 | 0.1 / 0.5 |
| Additional lights | Per-pixel | Per-pixel | Per-pixel | Per-pixel |
| Additional-light shadows | Off | Off | Off | Off |
| Depth texture | Off | Off | On | On |
| Opaque texture | Off | Off | As needed | As needed |
| Post profile | PP_Low | PP_Mid | PP_High | PP_Ultra |
| **FPS ceiling** | 30 | 30 | 60 | 60 |

**⚠️ Render scale is currently DEFERRED (all tiers = 1.0).** URP does not resize the camera's render
target reliably when render scale changes at startup/runtime — it produced blur + inverted results across
restarts and scenes (fine on a live switch with a camera-refresh, wrong on a fresh boot). Pulled out until
it can be re-introduced properly: applied only at a hard scene transition, or via URP dynamic resolution.
This does weaken the Low tier's low-end headroom (render scale is the biggest GPU lever), so re-adding it
correctly is a tracked follow-up. Tiers currently differ by MSAA / shadow res+softness / SSAO / HDR — all
of which apply cleanly, live and at startup.

**Why this ties everything together:**
- **Avatar shadows come from the MAIN light** (confirmed working), so every tier keeps a main-light
  shadow — Low/Mid at a cheap 1024, High/Ultra at 2048. `m_AdditionalLightShadowsSupported` stays
  **off on all tiers** (matches the confirmed-working setup — do not turn it on). The environment
  leans on the **baked lightmaps + probes** we built, so Low can run a small/short shadow cheaply.
- **Additional-lights rendering mode + bias are NOT tiered** — kept at the working Mobile_RPAsset
  values so nothing that currently looks right breaks. Only the high-impact levers move per tier.
- **SSAO is High/Ultra only** (the Advanced renderer) — costly *and* darkens characters (open TODO).
  Low/Mid never see the artifact.
- **Blob shadow is now OPTIONAL** — only needed if a future "potato" tier kills main-light shadows
  outright. Not a dependency of this 4-tier system.

Costs, per Unity's mobile guidance: MSAA burns bandwidth on tile GPUs; soft shadows are expensive on
tile GPUs (profile, don't assume); shadow-casting point lights ≈ 6× a spot (avoid); per-vertex
additional lights are much cheaper than per-pixel. (These were the verification claims truncated by
the research session limit — they're standard Unity URP mobile guidance, stated here as such, not as
freshly triple-verified.)

---

## 5. Asset structure

```
Assets/Settings/
├── PC_RPAsset            → PC_Renderer         (unchanged; Standalone quality level)
├── PC_Renderer
├── Mobile_Basic_Renderer     (Forward+, no SSAO, ShadowTransparentReceive)   ← rename of current Mobile_Renderer
├── Mobile_Advanced_Renderer  (Forward+, SSAO,  ShadowTransparentReceive)     ← new, copy of Basic + SSAO
├── Mobile_Low_URP     → Mobile_Basic_Renderer
├── Mobile_Mid_URP     → Mobile_Basic_Renderer
├── Mobile_High_URP    → Mobile_Advanced_Renderer
└── Mobile_Ultra_URP   → Mobile_Advanced_Renderer
```

Duplicate the **already-tuned** `Mobile_RPAsset` (our shadow fixes live in it) four times, then dial
each per the table. Duplicating preserves the working renderer reference; safer than fresh-create.

**Quality levels** (Project Settings ▸ Quality): create `Low / Mid / High / Ultra`, assign the four
URP assets; keep `PC`. Set Android/iOS default = Mid. Current state: only 2 levels exist today
(`Mobile`→Mobile_RPAsset, `PC`→PC_RPAsset).

---

## 6. Code architecture

- **`GraphicsQualityManager`** (new; persistent singleton, spawned in Boot) — single source of truth.
  - `enum GraphicsTier { Low, Mid, High, Ultra }`, `tier → qualityLevelIndex` map.
  - `Apply(tier)`: `QualitySettings.SetQualityLevel(index, true)` + set the FPS ceiling + fire
    `OnTierChanged`. Persists `PlayerPrefs["khela.gfxTier"]`.
  - `ResolveTier()`: saved pref > manual > **auto-detect** (§7).
  - `SetTier(tier)` for the Settings menu; `AutoTier()` for "Auto".
- **`PostFxTierController`** (existing) — drop its independent auto-detect; subscribe to
  `GraphicsQualityManager.OnTierChanged` and apply the matching PP profile. Add Ultra.
- **`SceneFrameRate`** (existing) — change `Apply()` to `targetFrameRate = min(sceneFps,
  GraphicsQualityManager.FpsCeiling)`. Retire `MobileBootstrap`'s hard 60 (the manager owns it now).
- **Blob shadow** — enabled on Low/Mid (additional-light shadows off there); real catcher on High/Ultra.

---

## 7. Automatic device detection

Research-confirmed traps: **RAM is a poor GPU proxy** (6 GB tablet + weak Adreno 610 example);
`SystemInfo.graphicsMemorySize` is unreliable on Android (differs Vulkan vs GLES). So the current
`DetectTier()` (RAM/VRAM/cores) is upgraded to **GPU-aware**:

1. **Primary: parse `SystemInfo.graphicsDeviceName`** for the GPU family/model (Adreno / Mali /
   PowerVR / Apple), binned against a maintained table:
   - Adreno 6xx-low (610/612/619), Mali-G5x → **Low**
   - Adreno 6xx-mid (640/642L/650), Mali-G7x-mid → **Mid**
   - Adreno 7xx, Mali-G78/Immortalis, Apple A13+ → **High**
   - Adreno 7xx-elite / flagship, Apple A16+ → **Ultra**
2. **Secondary: `processorCount`** as a tiebreaker; RAM only as a weak floor (never the primary gate).
3. **Runtime safety-net:** after the first real scene, sample FPS for a few seconds; if it sits below
   the tier's target, auto-drop one tier and re-save. Catches devices the table misses.

Ship the table as data (easy to update from telemetry). This is what shipping mobile games do — a
coarse GPU bin for the default, then trust the player's override + the runtime net.

**Adaptive Performance (later, Phase 2):** Unity's `com.unity.adaptiveperformance` + the Android
(Google) ADPF provider reads real thermal state (`getThermalHeadroom` / status) and auto-scales
(shadows → view distance → resolution) to prevent throttling and hold the FPS target on Android 12+.
It **coexists** with the manual menu (menu sets the ceiling; AP scales *down* under heat). Worth
adopting for the demanding **world** scenes once the static tier system ships. Not Samsung-only (that
claim was refuted). Sources: [Google ADPF](https://developer.android.com/games/engines/unity/unity-adpf),
[Unity AP blog](https://unity.com/blog/engine-platform/mobile-performance-optimization-with-adaptive-performance-40).

---

## 8. Settings-menu UX

- **Graphics Quality:** `Auto · Low · Mid · High · Ultra` (Auto = §7 detection).
- **Frame Rate:** `30 · 60` (60 disabled/greyed on Low/Mid-detected devices).
- **Battery Saver:** forces Low + 30 fps + render scale down; one toggle.
- **Advanced (optional, later):** per-option toggles — Shadows (Off/Low/High), Post (On/Off),
  Anti-aliasing (On/Off), Resolution Scale (Low/Med/High). These nudge the *active* URP asset via
  `GraphicsSettings.currentRenderPipeline` (the runtime-mutation complement) on top of the tier.
- **Graceful downgrade:** on a detected-weak device, grey out High/Ultra and show *"Some options are
  unavailable on this device."* Allow manual override above the detected tier but warn ("may run
  poorly"), and let the runtime safety-net drop it if it genuinely can't cope.

---

## 9. Build phases

- **Phase A — assets (editor):** rename `Mobile_Renderer`→`Mobile_Basic_Renderer`; create
  `Mobile_Advanced_Renderer` (Basic + SSAO); duplicate `Mobile_RPAsset` → 4 tier assets and dial each
  per §4; create 4 Quality levels + assign. *(I can generate the .asset files; level creation is
  cleanest in-editor.)*
- **Phase B — code:** `GraphicsQualityManager`; refactor `PostFxTierController` to subscribe; make
  `SceneFrameRate` clamp to the ceiling; add `PP_Ultra_Override`; build the blob shadow.
- **Phase C — Settings UI:** Quality dropdown + fps + Battery Saver wired to the manager.
- **Phase D — detection:** GPU-aware `DetectTier()` table + runtime fps safety-net.
- **Phase E — validate:** AFPS overlay on the Adreno 610 tablet + a mid + a flagship; tune the table.
- **Phase F (later):** Adaptive Performance for the world scenes.

---

## 10. Open decisions for Reza

1. **Ultra on mobile at all?** Ultra ≈ High + 4× MSAA + more cascades + high soft shadows. Real but
   niche on phones. Keep 4 tiers, or ship 3 (Low/Mid/High) and treat Ultra as a desktop/tablet-only
   flag? (Recommend: keep 4, but Ultra auto-selected only on true flagships.)
2. **Manual override above detected tier** — allow with a warning (recommended), or hard-cap?
3. **Adopt Adaptive Performance now or after ship?** (Recommend: after — get the static tiers +
   runtime net working and measured first.)
