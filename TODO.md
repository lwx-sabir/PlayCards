# Khela — TODO

Active, actionable task list. Broader strategy / phase gates live in [docs/PROJECT_PLAN.md](docs/PROJECT_PLAN.md) §6 & §13.

## Graphics / World scenes

- [ ] **SSAO darkens dynamic characters** — `PC_Renderer` (the renderer the *mobile* quality tier
      actually uses) has **Screen Space Ambient Occlusion at Intensity 0.4**. SSAO is full-screen, so it
      can't tell the avatar from the wall and darkens his creases (eye sockets, under-chin, neck, cloth
      folds) → dirty blotches on characters in the world scenes.
  - **Confirm:** toggle the SSAO renderer feature off; if the blotches vanish, that's it.
  - **Fix A (quick):** lower SSAO Intensity `0.4 → ~0.2`.
  - **Fix B (cleaner):** turn SSAO **off** + enable **Baked AO** in the Lighting settings
    (`m_AO`, currently off) so static geometry keeps contact shadows for free and the character stops
    getting dirtied — costs one re-bake, and drops the SSAO pass cost on mobile.
  - **File:** `khela/Khela.Play/Assets/Settings/PC_Renderer.asset`.

- [ ] **World-scene lightmap memory** — DiveBar_01's first bake produced **17 lightmaps @1024 +
      directional (~130 MB EXR source)** — far too heavy for mobile. Lower Lightmap Resolution
      `40 → ~12` texels/unit + Directional → **Non-Directional** (halves memory) + drop Contribute GI on
      small clutter props, then re-bake. See memory `world-scene-optimization`.

- [ ] **Player blob shadow** — no ground shadow under the dynamic player (baked lighting can't shadow
      dynamic objects). Add a mobile blob-shadow component (soft oval, raycast to floor, fade with
      height). *Offered — pending build.*

- [ ] **Run the world-optimization pipeline on the other 3 scenes** — DanceClub_01, NightClub_01,
      RooftopBar_01 (built identically to DiveBar_01). Flow: `Tools ▸ Khela ▸ World Prep ▸ 2 - Prep` →
      Ctrl+S → `Window ▸ Rendering ▸ Lighting ▸ Generate Lighting` → Ctrl+S → `World Prep ▸ 3 - Bake
      Occlusion`. (`Prep ALL` does the prep pass for all four at once.)

- [ ] **Graphics tier selection in Settings** — Low/Mid/High dropdown wired to
      `PostFxTierController.SetTier()` (persists to `PlayerPrefs["khela.gfxTier"]`). Post profiles +
      controller already built. Also tracked in PROJECT_PLAN §6.
