# Shop — Unity setup, step by step

*For `Assets/1Khela/_Scenes/Shop.unity`. The data layer and the binders are written; what is left is layout,
prefabs and dragging references. Nothing here needs a code change.*

---

## 0. What you are wiring to

**The server already ships these sections** (`chips` · `kash` · `packs` · `daily` · `piggy` · `pass` · `vip`) and
these products:

| lane | key | products today | notes |
|---|---|---|---|
| Chips | `chips` | 7 — `chips_01`…`chips_07` | $1.99 → $99.99, value/$ rising |
| Kash | `kash` | 6 — `kash_01`…`kash_06` | the parallel lane, never a bridge |
| Packs | `packs` | 1 — `starter_pack` | **ships disabled**; carries Chips + Kash + 300 VIP-P |
| VIP | `vip` | 7 — `vip_p_01`…`vip_p_05` + 2 boosters | VIP-P is the only way into a VIP level |
| Pass | `pass` | 1 — `golden_pass` | subscription; also sold from the pass popup |
| Piggy | `piggy` | 12 | **do not build a piggy lane** — see §9 |

**The scripts you will drop on things** (all in `PlayCard.Store`):

- `StorePurchaseButton` — one per CARD. Paints title, price, amount, bonus, two badges, sale ribbon, owned /
  unavailable / reason, and buys on tap.
- `StoreRewardLines` — optional, for BUNDLE cards. One row per thing the product pays.
- `ShopSection` — one per LANE. Binds your cards to that section's products, clones extras.
- `ShopScreen` — one, on the shop root. Fetch + loading / store-off states.
- `StoreRestoreButton` — one, anywhere in the shop. Apple requires it.

---

## 1. Decide the layout before you build it (paper, 5 minutes)

Settled already, do not re-open: **one screen**, **Chips and Kash as parallel lanes**, **no currency bridges**,
odds disclosed where any randomness is sold.

A layout that fits what the server actually has:

```
Shop (root)                     ← ShopScreen
├─ Loading                      ← loadingRoot
├─ Unavailable                  ← unavailableRoot (+ a TMP line for the reason)
└─ Content                      ← contentRoot
   ├─ Featured        (packs)   ← ShopSection, 1 card, wide
   ├─ Lane_Chips      (chips)   ← ShopSection, 7 cards
   ├─ Lane_Kash       (kash)    ← ShopSection, 6 cards
   ├─ Lane_Vip        (vip)     ← ShopSection, 7 cards (or its own sub-screen)
   └─ Footer
      └─ Button_Restore         ← StoreRestoreButton
```

Each lane wants a **layout group** on the container the cards sit in (Grid or Horizontal + `ContentSizeFitter`).
Cloned cards are inserted as siblings, so they only lay out correctly if the parent lays out its children.

---

## 2. Build ONE card prefab, properly, once

Everything else is a variant of it. Make it in the scene, then drag it to `Assets/1Khela/Prefabs/UI/Shop/`.

```
Card_Chips                    ← Button + StorePurchaseButton  (the whole card is the tap target)
├─ BG
├─ Icon
├─ Text_Amount                ← amountTexts      "5,000,000"
├─ Text_Title                 ← titleTexts       "Chip Stack"      (optional)
├─ Text_Bonus                 ← bonusTexts       "+33%"            (auto-hides at 0)
├─ Ribbon                     ← badgeRoot        the corner banner
│  └─ Text_Ribbon             ← badgeTexts       "BEST VALUE"
├─ Corner                     ← badge2Root       the hex mark, independent of the ribbon
│  └─ Text_Corner             ← badge2Texts      "POPULAR"
├─ Sale                       ← saleRoot         (whole group auto-hides with no sale)
│  ├─ Text_Sale               ← saleTexts        "-20%" / "+50%" / the sale's label
│  ├─ Text_StrikePrice        ← strikePriceTexts the REGULAR price, struck through
│  └─ Text_Countdown          ← saleCountdownTexts  ticks on the SERVER clock
├─ PriceBar
│  ├─ Text_Price              ← priceTexts       the store's localized price — never ours
│  └─ Loading                 ← loadingRoot      spinner while the store prices it
├─ Owned                      ← ownedRoot
├─ Unavailable                ← unavailableRoot
└─ Text_Reason                ← reasonTexts      the server's own words ("Limit reached")
```

**Inspector, on `StorePurchaseButton`:**

1. `productId` — type anything (`chips_01`). A lane overwrites it at runtime; it only matters for cards you
   place by hand outside a lane.
2. `root` — leave empty (defaults to this object). `purchaseButton` — leave empty if the `Button` is on the
   same object.
3. Drag every text/root above into its list. **All of them are optional** — a card without a ribbon just leaves
   `badgeRoot` empty; a missing label costs you a label, not the card.
4. `amountCurrency` — **this is the one that differs per variant**: `Chips`, `Kash`, or `VipPoints`. Empty = the
   product's first currency line.
5. `amountFormat` `N0`, `bonusFormat` `+{0}%` — leave as they are unless the design says otherwise.
6. Add **`ButtonSound`** and **`ButtonJuice`** beside it as usual. ⚠️ `ButtonSound` must fire on **PointerDown**,
   not on click.

---

## 3. The variants

| prefab | `amountCurrency` | extra |
|---|---|---|
| `Card_Chips` | `Chips` | — |
| `Card_Kash` | `Kash` | — |
| `Card_Vip` | `VipPoints` | needs a VIP-P icon in the art |
| `Card_Bundle` | leave empty | + `StoreRewardLines` (§4) — the starter pack pays three things |
| `Card_Pass` | leave empty | set `hideRootWhenOwned` **on**, so a subscriber stops seeing it |

For `starter_pack` also set `hideRootWhenOwned` — it is one-per-player, and a bought one-time offer that stays
on screen reads as a bug.

---

## 4. Bundle rows (only for `Card_Bundle`)

A bundle's rows are the **catalog's** decision, not the prefab's — an admin who adds Gems to the starter pack
must not need a client build.

1. Make a row prefab: `Row_Reward` with a `BundleRewardView` (an `Image` → `iconImage`, a `TMP_Text` →
   `amountText`). This is the same row the daily bundle uses — reuse that prefab if it already exists.
2. On the card, add a child `Rows` with a `VerticalLayoutGroup`.
3. Put `StoreRewardLines` on the card, set `rowPrefab` = `Row_Reward`, `rowsParent` = `Rows`, `maxRows` = 3–4.
4. Fill `icons`: one entry per `rewardId` — `Chips`, `Kash`, `Gems`, `Coins`, `VipPoints` — with its sprite.
5. Leave `productId` blank; the lane sets it, on the card and on this component together.

---

## 5. The lanes

Per lane, on the lane container:

1. Add **`ShopSection`**.
2. `sectionKey` — `chips` · `kash` · `packs` · `vip` (exactly the server's key).
3. `cards` — drag the authored cards in **the order the ladder should read**. ⚠️ It binds by **list order**, not
   by hierarchy order — a list in the wrong order shows the ladder in the wrong order.
4. `cardTemplate` — optional. Leave empty and it clones the last card in the list when the catalog has more
   products than you authored. If you do assign one, it can live anywhere (even disabled); extras are still
   inserted after your last authored card.
5. `clonesParent` — leave empty unless the cards are not direct children of one container.
6. `maxCards` — 12 is fine. It is the guard against a catalog edit pushing a ladder off screen.
7. Optional: `titleTexts` (takes the section's title from the catalog, so renaming a lane in the admin renames
   it in the app), `emptyRoot`, `hideWhenEmpty`.

**How many cards to author:** 7 chips, 6 kash, 7 vip, 1 packs. Author fewer and the rest are cloned; author
more and the surplus hides. Either is fine — matching today's counts just means nothing is cloned.

---

## 6. The screen

On the shop root, add **`ShopScreen`**:

- `contentRoot` → `Content`
- `loadingRoot` → `Loading`
- `unavailableRoot` → `Unavailable`, and drag its TMP line into `unavailableTexts`
- leave `forceRefreshOnOpen` and `refreshAfterPurchase` **on**

It shows exactly one of the three at a time, fetches on open (from the disk cache first, so it is instant), and
re-fetches after a granted purchase so a one-per-player pack flips to OWNED.

---

## 7. Restore purchases

On the footer button, add **`StoreRestoreButton`**. Drag the `Button`, an optional spinner (`busyRoot`), the
label (`labelRoot`), and a TMP line into `statusTexts`. Leave `hideWhenNoStoreRoot` empty — a greyed button
explains itself; a vanished one becomes a support ticket.

This is not optional for shipping: Apple rejects a build that sells a subscription with no restore affordance,
and the golden pass is one.

---

## 8. Testing it in the Editor

1. The store rail needs **Boot** — `IapService` lives in `Boot.unity` with `DontDestroyOnLoad`. Press Play in
   `Shop.unity` alone and you get the "unavailable" state, which is correct, not a bug.
2. In the Editor the platform resolves to **Fake** (Unity's fake store), and the **server accepts Fake only in
   Development** — so run the backend in Development or every redeem is refused.
3. Drop `StoreDevTester` on any object in the running scene and use its context menu: **Store ▸ Log state**
   prints platform, init state, catalog count and whether the store priced a product; **Store ▸ Buy product**
   drives a whole purchase without any UI.
4. What "working" looks like: cards show real prices, a tap opens the fake-store dialog, the balance HUD moves
   on its own (the redeem goes through `BalanceChangingAsync`), and the card's state settles.

---

## 9. Gotchas

- **No piggy lane.** The 12 piggy SKUs are tier-specific and are sold from the piggy popup for the player's own
  tier (`PiggyPurchaseBridge`). A raw piggy lane would let a player buy a rung that is not theirs.
- **Prices are the store's, never ours.** `UsdReference` in the catalog is for the server's own bookkeeping. The
  card always shows Google's / Apple's localized string.
- **A card decides nothing.** What a product pays, what it costs, and whether this player may buy it are server
  answers. If a card looks wrong, the catalog or the admin is where it is wrong.
- **`starter_pack` ships disabled** — enable it in the admin before expecting the Featured lane to fill.
- The lane never turns its own object off, only its children, so a lane that vanished entirely is your own
  `SetActive`, not the component.
