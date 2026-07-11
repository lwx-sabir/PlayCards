# Avatar Cosmetics Shop — spec (v1)

Status 2026-07-06: **export tool BUILT** (full DB-field authoring + palette grid + upload) ·
**server BUILT** (catalog/inventory/purchase/entitlement gate; migration `AddCosmeticsShop`
applied; 234 tests green) · client shop UI / wardrobe inventory-gating / multi-avatar NOT started.

## Product model (Reza)

- **Most SKUs are SINGLE items** — one customized piece (a top, a hat, shoes), exported
  individually, bought individually. Colour model per item:
  - **Fixed** (cheap): renders only in the designed colours; the buyer can't recolour it.
  - **Palette** (premium): the buyer gets the SKU's **swatch GRID** and may apply any swatch to
    ANY colour channel/layer of the cloth — like BoZo's main creator, but bounded to the grid
    the designer authored. (Replaces the earlier fixed-colorway idea.)
- **A few exclusive costumes are SETS** — full or partial (any subset of slots), one SKU,
  fixed designed colours (v1).
- **Some exclusives are FULL CHARACTERS** — a named premade avatar (base + face + shapes +
  outfit), sold whole → needs **multi-avatar per user** (phase: not started; `UserAvatars`
  table planned, active-slot pointer keeps `GET /api/avatar/me` working).
- Wardrobe stays per-slot; the parts grid shows **owned items + free basics** (inventory is
  the whitelist). Body palettes (skin/hair/eyes) stay free personalization.

## Non-negotiables (extends CLAUDE.md rules)

1. **Server-side everything**: catalog, inventory, equips are server truth; the client only
   renders the sanitized echo ("all cloths must be server side networked").
2. **Entitlement gate on avatar save** (BUILT): every cataloged outfit path must be owned;
   colours must be legal (Fixed = designed colours exactly; Palette = every channel from the
   grid). Uncataloged paths = free basics (bootstrap-friendly: empty catalog ⇒ everything free).
3. **Purchases through the wallet** (BUILT): debit idempotent on CorrelationId + grant unique
   per (user, sku) — retries never double-charge or double-grant. **Tokens pricing is rejected
   at import AND purchase** (dual-currency guardrail). Currencies: Coins/Gems/Kash/Chips.
4. Patterns/decals are OUT of the v1 contract.

## Catalog JSON (tool output == `POST /api/shop/cosmetics/import` body)

`khela/catalog/cosmetics.json` — camelCase, one `skus` array:

```json
{ "skus": [
  { "id": "top-leather-jacket", "type": "item", "name": "Leather Jacket",
    "description": "…", "gender": "", "slot": "Top", "path": "Top/BSMC_Top_LeatherJacket",
    "colorMode": "palette",
    "defaultColors": ["#111111", "#c0c0c0", "#333333"],
    "palette": ["#111111", "#8b0000", "#0a3d62", "#d4af37"],
    "price": 2500, "priceCurrency": "Coins",
    "isStarter": false, "exclusive": false, "enabled": true, "sortOrder": 10 },

  { "id": "set-high-roller", "type": "set", "name": "High Roller",
    "pieces": [ { "path": "Top/BSMC_Top_Tux", "colors": ["#0a0a0a"] } ],
    "price": 500, "priceCurrency": "Kash", "exclusive": true, "enabled": true },

  { "id": "char-vegas-vic", "type": "character", "name": "Vegas Vic",
    "character": { "gender": "Male", "baseId": "Base/John", "body": [], "face": [],
                   "mods": [], "outfits": [ { "path": "…", "colors": ["…"] } ] },
    "price": 1000, "priceCurrency": "Kash", "exclusive": true, "enabled": true }
] }
```

`id` = stable kebab key everywhere. `path` = BoZo Resources key (equip key). `defaultColors`
index = channel-1. `gender` = "Male" | "Female" | ""/omitted = unisex — filters the shop AND
gates equips server-side (a gender-locked SKU can't dress the other gender; character SKUs
derive it from their payload). Import is an UPSERT by id (re-import freely).

## Server (BUILT)

- `CosmeticSkus` (string PK id, type item/set/character, colour model, JSON payload columns,
  price decimal(18,4) + currency, starter/exclusive/enabled/sort, timestamps) and
  `UserCosmetics` (unique (UserId, SkuId), source, CorrelationId) — migration `AddCosmeticsShop`.
- `ICosmeticsService`/`CosmeticsService`: `GetCatalogAsync` (enabled + owned flags; starters
  implicitly owned), `ImportAsync` (validating upsert; rejects Tokens/negative price/bad shape),
  `BuyAsync` (debit→grant, both idempotent; insufficient funds → clean error; already-owned →
  no-op success), `ValidateEquipsAsync` (the entitlement gate; normalizes missing colours to
  the SKU defaults in place).
- `CosmeticsController`: `GET /api/shop/cosmetics` · `POST /api/shop/cosmetics/{id}/buy`
  `{correlationId}` · `POST /api/shop/cosmetics/import` (**Admin** policy; dev-open).
- `AvatarController.Save` now runs the entitlement gate after `AvatarSanitizer` — an unowned
  or illegally-coloured equip is a 400, never stored.

## Icons (BUILT — baked 3D product shots, server-hosted)

The exporter bakes a REAL product shot at export time: the piece(s) attached to a hidden
mannequin (body renderers off — garment only, like BoZo's own icons), SKU colours applied,
isolated layer, orthographic camera auto-framed to the garment bounds, transparent 512×512
PNG. Character SKUs photograph the whole dressed rig instead. Saved locally as
`khela/catalog/icons/<sku-id>.png`; **Upload** pushes the catalog first, then every icon to
`POST /api/shop/cosmetics/{id}/icon` (admin, PNG-validated, ≤1MB, stored in the DB row).
Serving: `GET /api/shop/cosmetics/{id}/icon` — anonymous + cacheable (ETag), so the Unity
shop, the web admin (`<img src>`), and any CDN read the same truth. Catalog + equip-gate
queries PROJECT past the blob column (`HasIcon` flag only). 100+ SKUs on one mesh is by
design: the mesh ships once in Resources; SKUs are rows + hex strings; only the icon PNG is
per-SKU.

## Editor tool (BUILT — `Khela ▸ Avatar ▸ Cosmetic Exporter`)

Authors EVERY DB field: id (+Load-back for editing), name, description, price, currency
(Tokens not offered), starter/exclusive/enabled/sortOrder. Item mode: piece picker, colour
mode, **palette grid editor** (swatch rows, +Swatch / +Piece's current colours / +Standard 12),
defaults captured from the rig. Set mode: tick pieces. Character mode: gender+base → full
AvatarDto. Merges into the catalog JSON; **Upload button** POSTs it to the import endpoint
(paste a JWT; dev admin is open). Editor-only — the BoZo creator scene never ships.

## Remaining phases

1. **Client shop UI** + wardrobe inventory-gating (`PartsForSlot` filtered by
   `GET /api/shop/cosmetics` owned flags) + palette-grid colour picker for owned premium items.
2. **Multi-avatar** (`UserAvatars` migration, slots endpoints, "My avatars" switcher; buying a
   character creates a slot).
3. Starter content pass (free basics per gender) + pricing pass in the admin dashboard.
