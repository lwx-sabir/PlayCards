# Invector → New Input System migration (3D world controller)

**Status:** PLANNED / DEFERRED. Package imported; tags/layers done. **Do this AFTER wardrobe v1.**
**Owner concern:** client only (`Khela.Play`). No server impact, no non-negotiable rules touched.
**Goal:** Use Invector's **Third Person Controller – Shooter Template** to drive the avatar in the 3D
social world, with **new-Input-System touch controls** — *without editing any Invector source file* and
*without importing Invector's legacy mobile add-on*.

The user chose the **full Shooter controller** (not basic locomotion) for long-term features
("everything, then decide equipped/empty-handed per context").

---

## TL;DR of the approach
1. Subclass the input component: **`KhelaShooterInput : vShooterMeleeInput`** in `PlayCard.*`.
2. Override the **leaf input methods** to read a new-Input `InputActionAsset` and feed `cc.input` /
   camera / shooter-manager. **Never call `base` on an overridden input read; never touch a
   `GenericInput` member** (reading one lazily spawns the legacy `vInput` singleton — see Gotchas).
3. Keep Invector's `InputHandle()` dispatch (it already does all armed/unarmed/hipfire branching).
4. Build the on-screen HUD with the **new Input System's built-in `OnScreenStick` / `OnScreenButton`**
   (+ a right-side touch-drag look region), on an EventSystem using `InputSystemUIInputModule`.
5. Leave **Active Input Handling = Both** as a safety net (your controls are new-Input; Both only keeps
   Invector's internal legacy reads from throwing). Going fully new-only is NOT worth it (see Gotchas).

---

## Two findings that simplify the mobile work
1. **Gun aim is CAMERA-based, not cursor-based.** `vShooterMeleeInput.AimPosition` comes from the camera
   forward + a raycast (~lines 1241-1291), i.e. a screen-center crosshair. It does **not** use
   `vMousePositionHandler`. That handler only fires for the **grenade/throw** system in top-down /
   side-scroll camera modes. → On mobile, "aim direction" is just the camera (our Look override); the
   crosshair is screen-center; **`vMousePositionHandler` can be ignored** unless we add throwables.
2. **`InputHandle()` already branches everything.** Every level overrides `InputHandle()` and calls
   `base.InputHandle()` + its own combat reads. We KEEP it and only replace the leaf reads — no dispatch
   logic to reimplement.

---

## Class chain (confirmed by reading the imported files)
```
vShooterMeleeInput : vMeleeCombatInput : vThirdPersonInput : vMonoBehaviour
  (also implements vIShooterIKController, vILockCamera, vIMeleeFighter)
```
Concrete input component on the Shooter-template player prefab = **`vShooterMeleeInput`** → so subclass
that. (Confirm the prefab actually uses this exact class when building.)

Files (imported, real paths):
- `Assets/Invector-3rdPersonController/Basic Locomotion/Scripts/CharacterController/vThirdPersonInput.cs`
- `Assets/Invector-3rdPersonController/Melee Combat/Scripts/CharacterController/vMeleeCombatInput.cs`
- `Assets/Invector-3rdPersonController/Shooter/Scripts/Shooter/vShooterMeleeInput.cs`
- `Assets/Invector-3rdPersonController/Basic Locomotion/Scripts/CharacterController/vInput.cs` (GenericInput/vButton/vAxis — the legacy reader; DO NOT route through it)
- `Assets/Invector-3rdPersonController/Basic Locomotion/Scripts/Generic/Utils/vMousePositionHandler.cs` (throw-aim only)
- `Assets/Invector-3rdPersonController/Basic Locomotion/Scripts/Camera/vThirdPersonCamera.cs` (pure float-sink; NO changes needed)

---

## The override seam (methods to override in `KhelaShooterInput`)

### Locomotion + camera — clean (from `vThirdPersonInput`)
| Override (line) | New-Input action | Preserve in the override |
|---|---|---|
| `MoveInput` (453) | **Move** (Vec2) | set `cc.input.x/z`; honor `lockMoveInput`; re-call `cc.ControlKeepDirection()`; **drop** the direct legacy `Input.GetKeyDown(toggleWalk)` (CapsLock) at line 464 |
| `CameraInput` (563) | **Look** (Vec2 drag), **Zoom** | `cameraMain.RotateCamera(x,y)` + `cameraMain.Zoom(scroll)`; honor `invertCameraInputHorizontal/Vertical`, `lockCameraInput` |
| `JumpInput` (531) | **Jump** | call `base.JumpConditions()` (stamina/grounded gating, reads no input) |
| `SprintInput` (501) | **Sprint** | keep the `useContinuousSprint ? down : held` branch; melee layer also cancels sprint while attacking |
| `CrouchInput` (509) | **Crouch** | keep the always-call `cc.AutoCrouch()` before `cc.Crouch()` |
| `StrafeInput` (493) | **Strafe** | shooter `Reset()` disables `strafeInput.useInput` (shares Tab w/ camera-side) — likely a no-op button |
| `RollInput` (551) | **Roll** | call `base.RollConditions()` |

Camera (`vThirdPersonCamera`) reads NO input — it's a pure sink via `RotateCamera(x,y)` / `Zoom(scroll)` /
`SwitchRight(bool)`. Camera migration lives entirely inside `CameraInput()`.

### Combat — mostly clean because leaves delegate to *public* methods
| Input | Invector leaf (line) | Override approach | Clean? |
|---|---|---|---|
| **Fire** | `ShotInput` (759) | feed `HandleShotCount(shooterManager.CurrentWeapon, Fire.IsPressed())` — `HandleShotCount(weapon, **bool**)` (796) is public and takes the button bool → auto/semi/charge behavior preserved | ✅ |
| **Reload** | `ReloadInput` (871) | `if Reload.pressed → shooterManager.ReloadWeapon()` (public) + optional `autoReload` styles | ✅ |
| **Scope** | `ScopeViewInput` (952) | thin → `EnableScopeView()`/`DisableScopeView()` (public, 986/1002) gated on `IsAiming` | ✅ |
| **Switch cam side** | `SwitchCameraSideInput` (908) | trivial → `SwitchCameraSide()` (public, 924) | ✅ |
| **Melee weak** | `MeleeWeakAttackInput` (melee 101) | thin → `TriggerWeakAttack()` + `MeleeAttackStaminaConditions()` | ✅* |
| **Melee strong** | `MeleeStrongAttackInput` (melee 123) | thin → `TriggerStrongAttack()` (public override at shooter 593 cancels reload) | ✅* |
| **Block** | `BlockingInput` (melee 145) | one-line gate: `isBlocking = Block.held && cc.currentStamina>0 && !cc.customAction && !isAttacking` | ✅* |
| **Aim (ADS)** | `AimInput` (602) | ⚠️ **the one hard one** — ~40 lines mixing the aim-held read with `isAimingByInput`, `cc.Strafe()`, walk-when-aiming, `headTrack`, `controlAimCanvas.SetActiveAim/DisableAim`, `rWeapon/lWeapon.SetActiveAim/SetActiveScope`, hipfire `_aimTiming`. Must be **faithfully re-implemented** against the real source and **adversarially verified in isolation**. | ⚠️ |

`* Confirm public accessibility of `TriggerWeakAttack()` / `isBlocking` / `isAttacking` when building (they're
used to push animator bools); adjust if protected.`

### NOT handled by this class (do NOT try to add here)
- **Weapon switching / holster / draw / next-prev weapon** → lives in a separate component
  (weapon handler / inventory / `vAmmoManager`), not `vShooterMeleeInput`. Wire it when we build the
  weapon-select UI.

---

## Full `InputActionAsset` — map "Player"
Locomotion/cam: `Move`(Value/Vec2), `Look`(Value/Vec2), `Zoom`(Value/axis, optional pinch),
`Jump` `Sprint` `Crouch` `Roll` `Strafe` (Button).
Combat: `Aim`(Button, hold), `Fire`(Button), `Reload`(Button), `Scope`(Button), `SwitchSide`(Button),
`MeleeWeak`(Button), `MeleeStrong`(Button, optional), `Block`(Button, hold).

Control schemes: **Touchscreen** (on-screen), plus **Keyboard&Mouse** + **Gamepad** for editor testing.
HUD shows a **contextual subset** (armed → aim/fire/reload/scope; unarmed → melee), never all at once.

Invector's default bindings for reference (what the GenericInput fields map to today):
- Move `Horizontal/Vertical`; Sprint `LeftShift`/`LeftStickClick`; Crouch `C`/`Y`; Jump `Space`/`X`;
  Roll `Q`/`B`; Strafe `Tab`/`RightStickClick`; camera `Mouse X/Y`/`RightAnalog*`; zoom `Mouse ScrollWheel`.
- Aim `Mouse1`/`LT`; Fire `Mouse0`/`RT`; Reload `R`/`LB`; SwitchSide `Tab`/`RightStickClick`; Scope `Z`/`RB`.
- Melee weak `Mouse2`/`RB` (reassigned in shooter `Reset()` so it doesn't clash with aim); block `Mouse1`/`LB`.

---

## Why "Both" stays ON (legacy stragglers we can't reach by overriding)
Verified by scanning the whole Basic Locomotion script set:
- **`vInput` singleton trap:** `vInput.OnGUI()` device auto-detect polls raw legacy `Input`
  (`GetMouseButtonDown`, `touches`, `GetAxis("Mouse X/Y")`, `GetKey(Joystick1ButtonN)`, analog axes)
  **every GUI frame whenever a `vInput` instance is alive** — independent of our overrides. Reading ANY
  `GenericInput` member (even a debug log) lazily spawns it via `new GameObject("vInputType")`.
  → Don't place a `vInput` component; never read a `GenericInput` member in the subclass.
- **`vInput.IsButtonAvailable()` (line 818, `Input.GetButton`)** runs at the start of *every* GenericInput
  read (wrapped in try/catch, degrades under new-only).
- **Interactables read through `vInput`:** `vLadderAction`, `vGenericAction`, `vGenericAnimation`,
  `vSimpleTriggerWithInput` all fire during normal play if present. → We use our **own interaction system**
  (table-join, doors) instead of Invector interactables, so these won't be in the scene.
- **`vMousePositionHandler`** reads legacy directly (`Input.mousePosition`, `GetTouch`, `GetAxis`) — but
  only for grenade/throw aim; skip unless we add throwables (then subclass its `mousePosition` getter and
  place the subclass in-scene before anything calls `Instance`).

Going fully new-only would additionally require porting `vInput.cs` and re-authoring every interactable —
not worth it. `Both` costs ~nothing at runtime and, on a phone, the legacy paths never fire anyway.

---

## Prereqs already DONE
- Base Shooter Template imported into `Assets/Invector-3rdPersonController/` (single self-contained folder;
  ships NO `ProjectSettings/`, so it did not overwrite our tags/layers/graphics/scene list).
- **Tags/layers added** to `ProjectSettings/TagManager.asset` (our `Avatar`/`Chip`/`BetSpot`/`Avatar_3d`
  kept; layer 31 left free for the icon booth):
  - Layers 8-13: `Triggers, Player, HeadTrack, Enemy, CompanionAI, BodyPart`
  - Tags: `Ignore Ragdoll, Weapon, Enemy, CompanionAI, AutoCrouch` (`Player`/`MainCamera` are built-ins)
- **Unity 6.3 compile fixes (unavoidable Invector-source edits):** the project is on **Unity 6000.3** (6.3),
  and Invector 84583's version guards (`#if UNITY_6000_2/3_OR_NEWER`) reference newer editor/runtime APIs that
  aren't actually present in this build. Collapsed each broken guard to its stable branch:
  - `vThirdPersonInput.cs` (~160, `FindCamera`) + `vShooterManager.cs` (~461, `GetAmmoDisplays`): the
    `UNITY_6000_2_OR_NEWER` branch called a non-existent 1-arg `FindObjectsByType<T>(FindObjectsInactive)` →
    collapsed to 2-arg `FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)`.
  - `Editor/vInvectorIcon.cs` (10-41): the `UNITY_6000_3_OR_NEWER` branch used
    `EditorApplication.hierarchyWindowItemByEntityIdOnGUI` + `EntityId`/`EntityIdToObject` (event missing in
    this build) → collapsed all 3 guards to the stable `hierarchyWindowItemOnGUI(int)` + `InstanceIDToObject`.
  These are compile-compat fixes, not input logic — the "no source edits" rule is about the migration seam.
  More may surface on this bleeding-edge Unity; fix each by collapsing its guard to the working branch.
- **Unity 6.3 Search-indexer NRE (runtime, non-fatal):** `Attributes/Editor/vHelpBoxDecorator.cs:8` had a
  field initializer `GUIStyle style = new GUIStyle(EditorStyles.helpBox);`. On 6.3 the Search asset indexer
  *constructs* property/decorator drawers off the GUI thread (to read tooltips), where `EditorStyles.helpBox`
  is null → NRE spam "Failed to build search index for …prefab". Fixed by removing the initializer (`OnGUI`/
  `GetHeight` already lazily build `style`). Rule for any future drawer: never touch `EditorStyles`/GUI in a
  `PropertyDrawer`/`DecoratorDrawer` field-init or ctor — only in `OnGUI`/`GetHeight`. Scanned the tree; this
  was the only offender (other `EditorStyles` uses are method-locals, lazy guards, or EditorWindow fields).
- **Invector onboarding nag disabled:** `Generic/Utils/Editor/vCheckForProjectSettings.cs` draws a
  Scene-view overlay ("Set ActiveInputHandling to Both / Import ProjectSettings") whose `Validation()`
  triggers when `NameToLayer("Player") != 8` **or** the legacy axis `LeftAnalogHorizontal` throws. Under our
  new-Input-only + manual-layer setup it can never clear, so `Validation()` was changed to `return false`.
- ⚠️ **Layer-index note (verify at migration time):** that nag hardcodes **Player == layer 8**, but we placed
  `Triggers`=8 / `Player`=9. Invector's *runtime* uses `LayerMask.NameToLayer("Player")` (name-based, so index
  is irrelevant to gameplay) — but if any runtime code hardcodes `1 << 8` / layer 8 for the player (ragdoll
  or hit masks), swap to `Player`=8 / `Triggers`=9 (harmless to our `Avatar`/`Chip`/`BetSpot`). Not needed now.

## Prereqs REMAINING (before/while building)
- Set **Player ▸ Active Input Handling = Both**, restart Unity. (Project is currently new-only, so Invector
  stragglers WILL throw until this is set.)
- Convert Invector materials to URP: **Edit ▸ Rendering ▸ Materials ▸ Convert Selected Built-in Materials to URP**
  (else magenta).
- **Do NOT** import the Invector mobile add-on (legacy CrossPlatformInput) — we replace it with new-Input.

---

## Build order (when we pick this up)
- **Slice 1** — `KhelaControls.inputactions` + `KhelaShooterInput` overriding the 7 locomotion leaves + the
  ✅-clean combat leaves (Fire/Reload/Scope/SwitchSide/Melee/Block) + on-screen stick/look/buttons canvas
  (`InputSystemUIInputModule`). Swap the player prefab's `vShooterMeleeInput` → `KhelaShooterInput`.
  → walkable + shootable on device.
- **Slice 2** — the ⚠️ `AimInput` (ADS) faithful override, verified in isolation.
- **Slice 3** — contextual HUD (show/hide buttons by armed/unarmed state), sensitivity tuning.

## File plan
- `Assets/1Khela/Input/KhelaControls.inputactions`
- `Assets/1Khela/Scripts/World/KhelaShooterInput.cs` (`namespace PlayCard.World` or similar)
- On-screen HUD canvas under the world scene (OnScreenStick → Move; right-side drag region → Look;
  OnScreenButtons → Jump/Sprint/Aim/Fire/Reload/…).

## Open items to verify when building
- Confirm the shooter player prefab's input component is exactly `vShooterMeleeInput` (subclass swap target).
- Confirm public accessibility of `TriggerWeakAttack()`, `isBlocking`, `isAttacking`, `MeleeAttackStaminaConditions()`.
- Faithfully re-implement + adversarially verify `AimInput()` (the only method with real reimplementation risk).
- Re-run the deferred adversarial "seam-verify" pass on the final wiring (the workflow's verifier was cut by a
  session limit; the three maps completed and are the basis of this doc).
