# IAP / Store — server + client spec (merged)

*Status (2026-08-22): SPEC, merged from two independent plans written the same night (`IAP_SPEC.md` "A" and
`IAP_STORE_PLAN.md` "B"; B is folded in here and deleted). Nothing is built yet. Both codebases and the WGWB reference
(`D:\Projects\WGWB\WGWB\Assets\_WGWB\Scripts\IAPService.cs` + `ShopService.cs`, Unity IAP 5.2.0, and
`D:\Projects\WGWB\UNITY_IAP_R8_RELEASE_INCIDENT.md`) were surveyed first. §13 lists every choice made in the merge —
those are the defaults; §14 is the brainstorm list.*

## 0. One-paragraph summary

Unity IAP 5.x (the WGWB `StoreController` flow, same public surface as WGWB's `IAPService` so buttons and habits port
1:1) buys the product; **the server verifies the receipt with the store's own API / signature chain and credits the
wallet**; the client confirms the order **only after** the server says *granted*. The catalog is **server-owned**
(Redis document `khela:store`, admin-edited, validated fail-closed, travels in the existing settings seed file) with one
stable product id and a store-product id per platform — a platform is "enabled" by filling its id and flipping
`Store:<Platform>:Enabled`; the client needs **no code change per platform** (Unity IAP picks the store module from the
build target). One server-side **purchase spine** (`StorePurchaseService`: reserve → verify → grant → complete,
idempotent on the store's transaction id) is fed by **one verifier per platform** behind `IStoreReceiptVerifier` and
fulfils products whose payload is the existing **`RewardGrant` line shape** plus a few **effects** (piggy break, golden
pass, VIP booster) behind `IStoreGrantHandler`. Every seam the codebase pre-built for this gets its caller:
`PiggyPanel.BreakRequested`, `PassPanel.SubscribeRequested`, `PiggyBreaks`, `PassService.GrantGoldenAsync`,
`VipService.RecordPurchaseAsync` / `ApplyVipBoosterAsync`, `LoyaltyMath.LpFromPurchase`, `ProgressionService.GrantXpAsync`,
`WalletContext.ExternalRef`.

**Config, not code,** to: add/change a product, change a grant, enable/disable a platform, kill the store. **Code** only
to add a store *vendor* (one verifier adapter) or a new product *effect* (one grant handler).

## 1. Principles (these decide every detail below)

1. **Server-authoritative grants.** The client never says how much it bought. The verified receipt names the store
   product; the catalog maps it to a grant; `IWalletService` does the credit. Client price metadata is informational.
2. **Idempotent on the store's transaction.** One store transaction = one `StorePurchases` row, **unique on
   `(Platform, StoreTransactionId)`** (Google = purchaseToken, Apple = transactionId). Every downstream leg (wallet
   credit, piggy break, pass window, VIP hook) has its own idempotency key derived from the row. Replays (client retry,
   crash between grant and confirm, pending-order re-delivery at next launch) return `AlreadyGranted` with no second
   credit — and a row that is Verified but not Completed is **re-driven**, so a paid purchase can't be lost either.
3. **Paid money is distinguishable from free money from the first receipt.** Credits are **`TransactionType.PaidPurchase`**
   (new, = 6; `Purchase` = 2 already means *in-game spend* — the cosmetics debit — and keeps that meaning),
   `ExternalRef = "{platform}:{storeTransactionId}"`, metadata with product / price / USD reference.
4. **Never leave paid money unfulfilled; never under-deliver.** Eligibility (limits, "piggy not full", "already golden")
   is enforced at **purchase intent**, before the store sheet opens. Once the store has charged, fulfilment always
   proceeds and only *flags* anomalies; if state moved under the player (piggy expired between tap and receipt) pay the
   tier capacity rather than refuse — a refusal on a charged card is a support ticket and a chargeback.
5. **Two parallel lanes, no bridges.** Real money buys **Chips** or **Kash** directly (a bundle may contain both — that
   is not a conversion). Tokens are never sold: the catalog validator reuses the `RewardCurrencies` fail-closed allowlist.
   Random real-money payloads (chest lines) are refused unless `Store:AllowRandomPayloads` — loot-box law (odds
   disclosure, BE/NL) is decided before such a SKU exists.
6. **Rail-agnostic.** Platform is a column + an adapter, never an `if`. Spend-side hooks receive *(USD, idemKey)*, not a
   receipt, so the later web store is a second feed and VIP/Loyalty need no change (PROGRESSION_SPEC).
7. **Price is the store's.** The client shows `product.metadata.localizedPriceString`; `UsdReference` is for SP/LP
   accounting, reports and admin. Never render our own price.
8. **House money rules hold.** All wallet writes via `IWalletService`; correlation ids ≤ 64 chars; new money tables carry
   `OperatorId`; nothing runs under a table/settle lock (purchases are outside the game path); Fake verifier impossible
   in Production.

## 2. Architecture

```
 Unity client (PlayCard.Store)                     Khela.Game (Services/Store)                          Stores
 ───────────────────────────────                   ─────────────────────────────────────────            ──────
 StoreCatalog ── GET /api/store/catalog?platform ─▶ StoreCatalog (Redis doc khela:store, 15s cache, per-user Purchasable)
      │  product ids + store ids for THIS platform
      ▼
 IapService (Unity IAP 5 StoreController)
   Connect → FetchProducts(defs) → Ready (localized prices) → FetchPurchases (re-drives pending orders)
   TryPurchase(productId) ── POST /api/store/intent ──▶ eligibility (limits/window/level/piggy-full/already-golden)
      ok → PurchaseProduct ───────────────────────────────────────────────────────────────────────────▶ Play / App Store
   OnPurchasePending(order) ── POST /api/store/redeem {platform, productId, storeProductId, txId, receipt} ─▶ StorePurchaseService
                                                      │ 1 RESERVE  row (Platform,TxId) unique → AlreadyGranted fast path
                                                      │ 2 VERIFY   IStoreReceiptVerifier[platform] ─────────────────▶ androidpublisher / App Store Server API / JWS
                                                      │ 3 CHECK    store product ↔ catalog, package/bundle id, IsTest
                                                      │ 4 GRANT    IStoreGrantHandler[effect]: PaidPurchase credits iap:{id}:{i}
                                                      │            + piggy break / golden window / VIP booster
                                                      │ 5 COMPLETE Granted + FulfilmentJson; Google acknowledge ──────▶
                                                      │ 6 hooks    VIP spend, LP drip, XP (best-effort, idempotent)
   ◀──────────────── {Ok, Status, Grants[], NewChipBalance, NewKashBalance, IsTest, Piggy?, Pass?} ────
   Granted | AlreadyGranted | Invalid → ConfirmPurchase(order)   (Pending / transient → keep pending, retry/backoff)
   BalanceChangingAsync → BalanceMaybeChanged → WalletManager → every HUD   ·   OnPurchaseCompleted → juice (Reza)
 StoreReconciliationService (hosted): re-drive stuck rows · Google ack/consume backstop · voided-purchases poll · sub refresh
 Webhooks (with the subscription): POST /api/store/webhooks/google (RTDN) · /apple (Server Notifications V2) → StoreEvents
 Khela.Web: Store ▸ Catalog editor · Purchases ledger · Platforms panel · Settings ▸ Store switches · revenue tiles
```

## 3. Catalog (`StoreCatalog`, Redis doc `khela:store`)

Same shape as `PassCatalog` / `ChestCatalog`: code `Defaults()`, admin-edited JSON document, `TryParse`, `Validate`
(first error or null, fail-closed), `ToJson`, ~15 s cache, and it **travels in `ConfigSeedFile.documents`** (add a
"Store catalog" group to Settings ▸ Transfer, CONFIG_TRANSFER.md) — no migration, no second seeder. Purchase rows never
depend on the live catalog (they keep a `CatalogSnapshotJson`), so editing it is safe at any time.

```jsonc
{
  "version": 1,
  "sections": [ {"key":"chips","title":"Chips"}, {"key":"kash","title":"Kash"}, {"key":"packs","title":"Packs"},
                {"key":"daily","title":"Daily"}, {"key":"piggy"}, {"key":"pass"}, {"key":"vip"} ],   // shop tabs (iap-shop-design)
  "products": [
    { "id": "chips_01", "enabled": true, "productType": "Consumable", "section": "chips", "sortOrder": 10,
      "title": "Chip Stack", "description": "", "badge": "", "bonusPercent": 0, "featured": false, "images": [],
      "storeIds": { "GooglePlay": "chips_01", "AppStore": "chips_01" },          // empty = not sold there; default = id
      "usdReference": 1.99,
      "lines": [ { "kind": 0, "id": "Chips", "amount": 5000000 } ],              // RewardGrant[]; a pack = several lines
      "effect": null,
      "availability": { "fromUtc": null, "toUtc": null, "maxPerUser": 0, "maxPerUserPerDay": 0, "minLevel": 0 } },
    { "id": "chips_02", "productType": "Consumable", "section": "chips", "sortOrder": 20, "bonusPercent": 33,
      "usdReference": 2.99, "lines": [ { "kind": 0, "id": "Chips", "amount": 10000000 } ] },
    { "id": "kash_01", "productType": "Consumable", "section": "kash", "usdReference": 1.99,
      "lines": [ { "kind": 0, "id": "Kash", "amount": 100 } ] },
    { "id": "piggy_t1_full", "productType": "Consumable", "section": "piggy", "usdReference": 1.99, "lines": [],
      "effect": { "type": "PiggyBreak", "arg": "Full" } },                         // tier resolved server-side at redeem
    { "id": "piggy_t1_x2", "productType": "Consumable", "section": "piggy", "usdReference": 3.99, "lines": [],
      "effect": { "type": "PiggyBreak", "arg": "FullDouble" } },
    { "id": "golden_pass", "productType": "Subscription", "section": "pass", "usdReference": 4.99, "lines": [],
      "effect": { "type": "GoldenPass", "arg": "monthly" } },
    { "id": "vip_booster_time", "productType": "Consumable", "section": "vip", "effect": { "type": "VipBooster", "arg": "Time" } }
  ]
}
```

Product ids are **the id Reza types on a button** (`chips_01…06`, `kash_01…03`, `starter_pack`, `piggy_t{n}_full|x2|early`,
`vip_booster_time|level`, `golden_pass`); store ids default to the same string on every platform (override only when a store
forces a different id). Validator: unique ids; unique store ids per platform; `productType` ∈ enum; every `lines` currency
passes `RewardCurrencies.TryParseAllowed` (Tokens and numeric ids refused); chest/random lines refused unless
`Store:AllowRandomPayloads`; `effect.type` must have a registered grant handler; a Subscription must carry an effect;
`usdReference ≥ 0`; a product with no store id on any enabled platform is a *warning* (authoring ahead of the consoles is
normal). **Value guard** (PIGGY_BANK_SPEC §6): refuse a piggy tier whose chips-per-USD is *worse* than the best chip pack — the
bank is, by design, the best chips-per-dollar offer in the game (paid, but earned by play); the store ladder's whale anchor
therefore stays at or below the piggy's per-$ (admin sees both numbers on save).

### 3.1 Agreed numbers (Reza, 2026-08-22 brainstorm) — the catalog defaults
World: tables up to **1M max bets**, tournaments with **2–5M** buy-ins/rewards (knockout etc.); free grind stays at the 1k–25k
tables. These are the store **minimums**; the upper rungs keep value/$ rising but **never above the piggy's ~5M/$** (value guard).

| product | chips / Kash | price | value |
|---|---|---|---|
| `chips_01` (entry) | 5M | $1.99 | 2.5M/$ |
| `chips_02` | 10M | $2.99 | 3.3M/$ |
| `chips_03` … `chips_07` (proposed) | 18M · 40M · 90M · 240M · 500M | $4.99 · $9.99 · $19.99 · $49.99 · $99.99 | 3.6 → 5.0M/$ (whale anchor 500M = 500 max-bets) |
| `piggy_t1_full` / `piggy_t1_x2` | 10M / 20M (bank fills in ~3 days) | $1.99 / $3.99 | **5.0M/$** — the best offer in the game, at a $1.99 price point (~2× the store) |
| piggy tiers 2–4 (proposed) | 20M / 40M / 100M (×2 at 2× price) | $3.99 / $7.99 / $19.99 | ≈ 5M/$ each |
| `kash_01` (entry) | 100 Kash | $1.99 | 1 Kash ≈ $0.02 |
| Kash ladder (proposed) | 275 · 600 · 1,300 · 3,500 · 7,500 | $4.99 · $9.99 · $19.99 · $49.99 · $99.99 | modest premium-currency bonuses |
| Chips → Kash exchange | **1,000,000 : 1**, one-way (DECISIONS E-03) | — | converting bought chips = 2.5 Kash/$ vs 50 Kash/$ direct → 20× worse, never an arbitrage |

Piggy pacing to make "3 days" true at every stake: `PiggyTier` gets its own `RatePercent` (tier 1 ≈ 500%, falling as
stakes rise — accrual mints nothing, it is pure pacing) and `Piggy:MaxAccrualPerDayPercent` 25 → 34 (25 = a 4-day floor).
Prerequisite batch (DECISIONS E-09): stake tiers extended to the 1M room + tournament lobby; level-up / daily / pass /
mission / chest / loyalty / starter / gift amounts re-denominated so the entry pack isn't dust and the free flow isn't a
Kash faucet (see §14). The Golden pass's
store ids are owned here; `PassProgram.GoldenProductIdApple/Google` become display-only and the validator warns on mismatch.

## 4. Data model (migration `AddStoreTables`)

### 4.1 `StorePurchases` — one row per store transaction; THE money event (`OperatorId` mandatory, money-path-red-lines)

| column | type | notes |
|---|---|---|
| `Id` | Guid PK | root of every idempotency key |
| `OperatorId` | varchar(64) | `Tenant.Default` |
| `UserId` | Guid | index (UserId, CreatedAt) |
| `Platform` | int | `StorePlatform` enum, APPEND-ONLY: `Unknown=0, GooglePlay=1, AppStore=2, Web=3, Amazon=4, Fake=99` |
| `ProductId` | varchar(64) | our catalog id |
| `StoreProductId` | varchar(128) | the platform product id bought |
| `StoreTransactionId` | varchar(256) | Google **purchaseToken** (globally unique; `orderId` can be null on test/promo purchases); Apple `transactionId`; Fake `fake:{guid}`. **UNIQUE (Platform, StoreTransactionId)** |
| `StoreOrderId` | varchar(128) null | Google `orderId` (`GPA.…`), Apple `transactionId` |
| `OriginalTransactionId` | varchar(128) null | Apple `originalTransactionId`; Google subscriptions = sha256-hex of the token (renewals keep the token) |
| `ProductType` | int | Consumable / NonConsumable / Subscription |
| `Status` | int | `Pending=0 → Verified=1 → Granted=2 · Invalid=3 · Refunded=4 · Revoked=5 · Expired=6` |
| `LastError`, `Attempts` | varchar(500), int | transient verify errors / re-drive count |
| `IsTest`, `Environment` | bool, varchar(16) | Google `purchaseType==0`, Apple Sandbox, Fake — excluded from revenue + spend hooks |
| `ObfuscatedAccountId` | varchar(64) null | Google's echo of `sha256(userId)`; Apple `appAccountToken`; mismatch = fraud signal, never a gate |
| `RegionCode`, `CountryCode` | varchar(8) | store billing region / `ApplicationUser.CountryCode` |
| `ClientPriceMicros`, `ClientPriceCurrency` | bigint, varchar(8) null | client-reported localized price — informational |
| `UsdReference` | decimal(18,4) | catalog reference AT SALE — what spend hooks and revenue tiles use |
| `CatalogSnapshotJson` | text | the product as reserved — fulfilment reads THIS |
| `RawReceipt` | longtext | unified receipt / JWS, capped `Store:MaxReceiptBytes` (64 KB), never logged at Info |
| `VerifierJson`, `FulfilmentJson` | text | store API / JWS decoded evidence; `GrantedLineDto[]` + effect results |
| `ClientPurchaseId`, `ClientVersion` | varchar(64), varchar(32) | funnel / support |
| `AcknowledgedAt`, `SubscriptionExpiresAt`, `RefundedAt`, `RefundSource` | | Google ack; sub window; refund audit |
| `CreatedAt`, `VerifiedAt`, `GrantedAt`, `CompletedAt`, `UpdatedAt`, `RowVersion` timestamp(6) `DateTime?` | | never `byte[]` |

Indexes: unique (Platform, StoreTransactionId); (UserId, CreatedAt); (Status, CreatedAt); (ProductId, CreatedAt); (OperatorId, CreatedAt).

### 4.2 `StoreEvents` — every store webhook / poll finding, idempotent
`Id`, `Platform`, `EventId` varchar(128) **UNIQUE (Platform, EventId)** (Pub/Sub `messageId`, Apple `notificationUUID`,
poll = `voided:{orderId}`), `EventType`, `StoreTransactionId` (256, null), `PurchaseId` (Guid null), `RawJson` longtext,
`ReceivedAt`, `ProcessedAt`, `Error` varchar(512).

### 4.3 `PiggyBreaks` (exists, never written) — extend
Add `Option int` (`PiggyBreakOption`), `Multiplier decimal(18,4)`, `BankedAmount`, `PayoutAmount decimal(18,4)`,
`StorePurchaseId Guid?`. `PurchaseId` (unique, ≤256) = `"{platform}:{storeTransactionId}"`. `PiggyTier` config gains
`PriceSkuDouble` + `PriceSkuEarly` beside `PriceSku` (Redis `Piggy:Tiers` JSON + admin tier editor columns);
`PiggyStateDto` appends them. This is the "SKU, multiplier and both figures" PROJECT_PLAN Stage-A asks for.

### 4.4 Ledger + enum
`TransactionType.PaidPurchase = 6` (append-only; pin with `TransactionTypeTests` like `CurrencyTests`). Wallet keys per
purchase: line *i* → `iap:{Id:N}:{i}` (39 chars); VIP → `iap:{Id:N}`; Loyalty → `iaplp:{Id:N}`; XP → `iapxp:{Id:N}`; refund
reversal = `RollbackAsync` of the original key. **Drop the dead `StoreItems` table/model** (`DbSet` only, never read or
written) in the same migration — two "store" tables is a trap.

## 5. Purchase flow (the money path)

### 5.1 `POST /api/store/intent {productId}` → `{ok, storeProductId, reason}`
`Store:Enabled`, platform enabled, product enabled + window + `minLevel` + `maxPerUser/PerDay` (count query), piggy SKU →
`CanBreak` (Early is allowed when not full by definition), `golden_pass` → not already golden, booster → applicable.
Called by the client before opening the store sheet. Nothing durable is written (a Redis funnel marker at most).

### 5.2 `POST /api/store/redeem` → `StorePurchaseService.RedeemAsync` (`[RequestSizeLimit(64*1024)]`, 200-always with `Ok`+`Status`)
```
a. auth → userId; kill switches (Store:Enabled, Store:<Platform>:Enabled — Redis-overlaid)
b. parse Unity unified receipt {Store, TransactionID, Payload} (Google) or take the Apple jwsRepresentation; Store must match Platform
c. FAST PATH: row (Platform, TxId) exists & Granted → AlreadyGranted (+ balances) — no store call
d. RESERVE: INSERT StorePurchases Status=Pending (+CatalogSnapshotJson, RawReceipt, UsdReference). The unique index is the
   mutex; the loser re-reads: Granted→AlreadyGranted · Pending&fresh(<60s)→Pending · Pending/Verified&stale→resume this row
e. VERIFY: registry[platform].VerifyAsync → {Outcome Valid|Pending|Invalid|Transient, StoreProductId, TxId, OrderId,
   OriginalTxId, IsTest, Environment, Acked, Consumed, AccountId, PurchaseTimeUtc, Subscription{Starts,Expires,AutoRenew}, Raw}
   Transient → LastError, Attempts++, row stays Pending, return Ok=false Transient=true (client backoff; reconciler retries)
   Pending   → Status stays Pending, return Status=Pending (Google deferred payment; poll/RTDN finishes it)
   Invalid   → Status=Invalid + reason (client confirms to stop re-delivery; row + receipt kept; admin re-drive after a config fix)
f. CHECK: verified store product == catalog storeIds[platform] of ProductId (client ProductId is a hint);
   packageName/bundleId == config; product enabled. Eligibility is NOT re-enforced here — money taken ⇒ fulfil, flag LimitExceeded.
g. GRANT via IStoreGrantHandler[effect] (each leg idempotent on its own key; a throw leaves the row Verified → re-drive):
   lines      → IWalletService.CreditAsync(user, currency, amount, PaidPurchase, "iap:{id}:{i}",
                 WalletContext{ExternalRef="{platform}:{txId}", Description="Store {ProductId}", MetadataJson})   (not CurrencyGranter: it types Bonus)
                 non-currency kinds → IRewardGrantService.GrantOneAsync
   PiggyBreak → PiggyService.BreakAsync(user, option, purchaseId, sku, storePurchaseId)
                 payout = Full→banked×1 · FullDouble→banked×2 · Early→MaxAmount×1; Full/×2 on a bank that expired/isn't full at redeem → basis MaxAmount (principle 4)
                 credit PaidPurchase, PiggyBreaks row (all figures), reset bank to next tier, return state + NewChipBalance
   GoldenPass → PassService.GrantGoldenAsync(user, passKey, "iap", purchaseRef=OrderId/transactionId, startsAt, expiresAt, originalTxId, autoRenew)
   VipBooster → VipService.ApplyVipBoosterAsync(user, kind, "iap:{id}")
h. COMPLETE: Status=Granted, FulfilmentJson, CompletedAt. Google: purchases.products.acknowledge (one-time) /
   subscriptions.acknowledge if !Acked — defuses the 3-day auto-refund of un-acknowledged purchases (money leak if the client never confirms)
i. outside the tx, best-effort, idempotent, skipped when IsTest (unless Store:TestPurchasesFeedSpend):
   vip.RecordPurchaseAsync(user, UsdReference, "iap:{id}") · loyalty.RecordPurchaseAsync(user, UsdReference, "iaplp:{id}") (new thin
   method over LpFromPurchase; inert while Loyalty:LpPerUsd=0) · progression.GrantXpAsync(user, floor(usd×Store:XpPerUsd), "store",
   "iapxp:{id}", bypassDailyCap:true) (XpPerUsd=0 default) · Serilog "StorePurchaseGranted" {UserId, ProductId, Platform, Usd, IsTest}
→ 200 {Ok, Status, PurchaseId, ProductId, Grants[], NewChipBalance, NewKashBalance, IsTest, Piggy?, Pass?}
```
Client: `Granted | AlreadyGranted | Invalid` → `ConfirmPurchase(order)`; `Pending | PlatformDisabled | StoreDisabled |
Transient/timeout` → keep the order pending, retry with backoff (2→30 s, bounded) and again on next launch.

Rules that fall out: **never confirm on a transient error** (Unity re-delivers pending orders each launch, the server is
idempotent — that pair makes a crash between grant and confirm safe; confirming early is the only way to lose a paid
purchase); **pending orders can arrive before auth is ready** (`OnPurchasesFetched` fires at `Connect`) → queue until
`AccountManager.IsReady`, bounded; one redeem per transaction id in flight; receipt/token never at Info level; account
binding = `SetObfuscatedAccountId(sha256(userId))` / `SetAppAccountToken(Guid userId)`, mismatch logged, grant anyway.

### 5.3 `POST /api/store/restore {platform, receipts[]}` — subscriptions / non-consumables through the same spine (idempotent) → entitlements.
### 5.4 `GET /api/store/purchases?take=` — my history (support, restore UI). `GET /api/store/catalog?platform=` — §3 for this platform + per-user `Purchasable`/`PurchasedCount`.

### 5.5 Refunds / lifecycle
- v1: `StoreReconciliationService` polls Google `purchases.voidedpurchases.list` (`Store:GooglePlay:VoidedPollMinutes`, 360)
  → `Refunded`, `RefundSource="google-voided"`; Apple refunds arrive with the webhook (below) or the App Store Server API
  refresh.
- Webhooks (ship **with the Golden subscription**, needed for renew/expire anyway): `POST /api/store/webhooks/google` (RTDN via
  Pub/Sub push: `?token=` shared secret + OIDC JWT audience check; `DeveloperNotification` → `oneTimeProduct` / `subscription` /
  `voidedPurchase` notifications) and `POST /api/store/webhooks/apple` (Server Notifications V2: verify `signedPayload` JWS;
  `REFUND`, `REVOKE`, `DID_RENEW`, `EXPIRED`, `DID_CHANGE_RENEWAL_STATUS`, `CONSUMPTION_REQUEST`, `TEST`). Both
  `[AllowAnonymous]` + signature-verified (the `AdsController` SSV pattern), idempotent via `StoreEvents`.
- Apply: renewal → `GrantGoldenAsync` (new window, same original id); expiry/revoke → `RevokeGoldenAsync`; one-time refund →
  `Status=Refunded` + policy `Store:Refunds:Policy`: **`Rollback`** (default) = per credited line `IWalletService.RollbackAsync`
  (compensating `Refund` row, never negative; if already spent it throws → fall back to `Flag`) or `Flag` (record + per-user
  refund count, no money moves). Piggy/VIP effects are never unwound; pass nodes already collected are never clawed back.

## 6. Verifiers — one class per platform, selected by config

```csharp
public interface IStoreReceiptVerifier
{
    StorePlatform Platform { get; }
    Task<ReceiptVerification> VerifyAsync(RedeemPurchaseRequest req, CancellationToken ct);   // never throws for a bad receipt → Invalid + reason
    Task AcknowledgeAsync(StorePurchase p, CancellationToken ct);                              // no-op where N/A
    Task<ReceiptVerification> RefreshAsync(StorePurchase p, CancellationToken ct);             // re-check (refunds, subscriptions)
}
```
`StoreVerifierRegistry.Resolve(platform)` → verifier or `PlatformDisabled`; built from DI + `Store:<Platform>:Enabled` + whether
credentials load (logged at startup). A disabled platform can still **re-drive / refund rows it already created**.

- **GooglePlayReceiptVerifier** (+ `IGooglePlayGateway` so tests fake the API; NuGet `Google.Apis.AndroidPublisher.v3`, service
  account JSON path from config, same copy-to-output pattern as `firebase-service-account.json`). Input = Unity's unified
  receipt `order.Info.Receipt`: `{"Store":"GooglePlay","TransactionID":"…","Payload":"{\"json\":\"{…}\",\"signature\":\"…\",\"skuDetails\":[…]}"}`
  → parse `Payload.json` → `purchaseToken, productId, packageName, orderId, purchaseState, obfuscatedAccountId`; cheap pre-checks
  (`packageName == Store:GooglePlay:PackageName`, product ∈ catalog, optional RSA check with `LicensePublicKey`); authority =
  `purchases.products.get(package, productId, token)` (one-time: `purchaseState` 0 ok / 1 cancelled → Invalid / 2 → Pending;
  `purchaseType` 0 → IsTest; `orderId`, `regionCode`, `obfuscatedExternalAccountId`, `acknowledgementState`) or
  `purchases.subscriptionsv2.get(package, token)` (`subscriptionState`, `lineItems[].expiryTime`, `startTime`, `linkedPurchaseToken`).
  `AcknowledgeAsync` = `purchases.products.acknowledge` / `consume` backstop.
- **AppStoreReceiptVerifier** — Unity IAP 5 = **StoreKit 2**: input `order.Info.Apple.jwsRepresentation` (the unified `Receipt`
  for Apple is the *whole app receipt*, not the order; `verifyReceipt` is deprecated). Verify the JWS locally (ES256, `x5c` chain
  to the bundled Apple Root CA G3) → payload `bundleId == Store:AppStore:BundleId`, `productId`, `transactionId`,
  `originalTransactionId`, `type`, `purchaseDate`, `expiresDate`, `revocationDate == null`, `environment` (Sandbox accepted,
  flagged IsTest — App Review buys with sandbox accounts on the production build), `appAccountToken`, `price`/`currency`/`storefront`.
  `RefreshAsync`/refunds via App Store Server API `GET /inApps/v1/transactions/{id}` (ES256 JWT from IssuerId/KeyId/.p8).
  Implementation: **`AppStoreServerLibraryDotnet`** (getmimo — port of Apple's official library) wrapped behind the interface.
  Built in v1 but **ships `Store:AppStore:Enabled=false`** until an iOS build exists.
- **FakeStoreReceiptVerifier** — Unity's Editor FakeStore receipt (`"Store":"fake"`): `IsTest=true`, `TxId=fake:{TransactionID}`.
  Registered **only when `IHostEnvironment.IsDevelopment() && Store:Fake:Enabled`** — gate on the environment, not just config,
  because the Redis overlay can flip config fields. This is what makes the whole pipeline testable in the Editor, and the one
  verifier that *would* be a free-chip faucet.
- **Web** (later: Stripe/Paddle/bKash/Nagad hosted checkout → webhook → same `RedeemAsync`) and **Amazon** (if ever): enum slot +
  adapter + store id in the catalog; spine untouched.

## 7. Server files, config, admin, tests

| File | Contents |
|---|---|
| `Khela.Common/Store/StoreDtos.cs` (netstandard2.1: no records/init) | `StorePlatform`, `StoreProductType`, `RedeemStatus { Granted, AlreadyGranted, Invalid, Pending, ProductUnavailable, PlatformDisabled, StoreDisabled, Error }`, `StoreProductDto` (Id, StoreProductId, ProductType, Section, Title, Description, Badge, BonusPercent, Lines[], Effect, UsdReference, SortOrder, AvailableToUtc, Purchasable, PurchasedCount, MaxPerUser, Images), `StoreCatalogDto { Platform, Sections, Products, ServerTimeUtc, Version }`, `StoreIntentRequest/ResultDto`, `RedeemPurchaseRequest { Platform, ProductId, StoreProductId, TransactionId, Receipt, Jws, ClientPriceMicros, ClientPriceCurrency, ClientPurchaseId, ClientVersion }`, `RedeemPurchaseResultDto { Ok, Status, Error, PurchaseId, ProductId, Grants[], NewChipBalance, NewKashBalance, IsTest, Piggy, Pass }`, `StoreRestoreRequest/ResultDto`, `StorePurchaseDto`. ⚠ rebuild `Khela.Common` Release + copy `Khela.Play/Assets/Plugins/Khela.Common.dll`. |
| `Services/Store/StoreConfig.cs` | `Store` section + `Overlay(cfg, redis)` for the **switches only** (`Store:Enabled`, `Store:<Platform>:Enabled`, `TestPurchasesFeedSpend`, `XpPerUsd`, `Refunds:Policy`, `AllowRandomPayloads`); credentials/paths never overridable |
| `Services/Store/StoreCatalog.cs` | §3: defaults, parse, validate (+ value guard), `StoreIdFor(product, platform)`, `ResolveByStoreId`, cache |
| `Services/Store/StoreMath.cs` | pure, DB-free: `CorrelationIdFor(id, line)` (asserts ≤64), `ParseUnifiedReceipt`, `ParseGooglePayload`, eligibility predicates, piggy payout table |
| `Services/Store/Verification/*` | `IStoreReceiptVerifier`, `StoreVerifierRegistry`, `GooglePlayReceiptVerifier` + `IGooglePlayGateway`, `AppStoreReceiptVerifier`, `FakeStoreReceiptVerifier` |
| `Services/Store/Grants/*` | `IStoreGrantHandler { string Effect; GrantAsync(row, snapshot, …) }`: `LinesGrantHandler` (currency/bundle), `PiggyBreakGrantHandler`, `VipBoosterGrantHandler`, `GoldenPassGrantHandler` |
| `Services/Store/StorePurchaseService.cs` | `IntentAsync`, `RedeemAsync` (§5.2), `RestoreAsync`, `GetHistoryAsync`, `MarkRefundedAsync(platform, txId, source)`, `RedriveAsync(id)` |
| `Services/Store/StoreReconciliationService.cs` (hosted, `SettlementReconciliationService` pattern) | every `Store:ReconcileIntervalSeconds` (120): re-drive Pending/Verified rows older than 2 min (backoff); Google ack/consume backstop for Granted-but-unacked rows; voided-purchases poll; golden windows ending within 24 h refreshed via API (hourly) so a missed webhook can't leave a dead sub golden; executes admin re-drive requests queued in Redis (`khela:store:redrive`) |
| `Services/Piggy/PiggyService.cs` | **add** `BreakAsync` (§5.2 g) + `PiggyMath.Payout`; `PiggyController`: `POST /api/piggy/break` works **only** under `Piggy:BypassPurchase` (dev; purchaseId `dev:{guid}`) — production = the store redeem |
| `Services/Loyalty/LoyaltyService.cs` | **add** `RecordPurchaseAsync(userId, usd, idemKey)` over `LpFromPurchase` |
| `Controllers/StoreController.cs` (`[Route("api/store")] [Authorize]`) | `GET catalog?platform=` · `POST intent` · `POST redeem` · `POST restore` · `GET purchases?take=` · admin (`[Authorize(Policy="Admin")]`): `GET/PUT catalog`, `POST redrive/{id}`, `GET admin/purchases` · webhooks `[AllowAnonymous]` (with the subscription) |
| `Program.cs` | catalog + purchase service scoped; verifiers + gateway singletons; grant handlers scoped (`IEnumerable<IStoreGrantHandler>`); `AddHostedService<StoreReconciliationService>()`; `TransactionType.PaidPurchase` |
| Migration `AddStoreTables` | `StorePurchases`, `StoreEvents`, `PiggyBreaks` columns, drop `StoreItems`. Apply LOCAL (me) + VPS (Reza, idempotent script — `khela-deployment-server`) |

### 7.1 `appsettings.json` — `Store` section (secrets stay out of git; file paths beside `firebase-service-account.json`)
```jsonc
"Store": {
  "Enabled": true,
  "TestPurchasesFeedSpend": false,
  "AllowRandomPayloads": false,
  "XpPerUsd": 0,
  "MaxReceiptBytes": 65536,
  "ReconcileIntervalSeconds": 120,
  "Refunds": { "Policy": "Rollback" },                       // Rollback | Flag
  "GooglePlay": { "Enabled": true, "PackageName": "com.casuallabinteractive.khela",
                  "ServiceAccountJsonPath": "google-play-service-account.json", "LicensePublicKey": "",
                  "AcceptTestPurchases": true, "AcknowledgeOnGrant": true, "SweepUnacknowledgedAfterHours": 24,
                  "VoidedPollMinutes": 360, "PubSubToken": "" },
  "AppStore":   { "Enabled": false, "BundleId": "com.casuallabinteractive.khela", "AcceptSandbox": true,
                  "RootCertPath": "AppleRootCA-G3.cer", "IssuerId": "", "KeyId": "", "PrivateKeyPath": "" },
  "Fake":       { "Enabled": true },                          // Development only; ignored (and logged) elsewhere
  "Web":        { "Enabled": false }
}
```
Switches are Redis-overlaid (Settings ▸ Store tab, instant, no restart) and travel with the seed file; turning a platform **on**
additionally requires its credentials to load.

### 7.2 Khela.Web
`StoreController` + `Views/Store/*` (sidebar "Store" under Operations): **Catalog** (cards/table + edit modal over the
`khela:store` document — ids, per-platform store ids, reference price, lines, effect, availability, badge/images; validate on
save; enable toggle; value-guard readout), **Purchases** (search by user / order id / status; IsTest + refund markers; detail =
receipt / verifier / fulfilment / events; actions: *Re-drive* (Redis queue → reconciler) and *Mark refunded (manual)* — the web
process never pays), **Platforms** panel (config loaded? credentials resolve? last poll / last webhook), **Settings ▸ Store**
switches, Settings ▸ Transfer group "Store catalog", **Testing** page: "Redeem fake product for user" (dev only — whole path
without a device). Dashboard tiles: revenue Σ `UsdReference` (test excluded) by day / product / platform.

### 7.3 Tests (`Khela.Game.Tests`; real-MySQL fixture `[Collection("khela-db")]` — ⚠ never while Reza's server runs)
`StoreCatalogTests` (Tokens refused, dup ids, handler registry, random-payload gate, value guard) · `StoreMathTests` (store id
per platform, correlation ids ≤64, receipt/payload parsing, eligibility, piggy payouts) · `TransactionTypeTests` (pins 0..6) ·
`StorePurchaseServiceTests` (replay → one ledger row + `AlreadyGranted`; 10 concurrent redeems of one tx → exactly one credit;
invalid → no credit, row `Invalid`; disabled platform → nothing written; unknown store id → `ProductUnavailable`; crash between
verify and grant → re-drive pays once; `IsTest` skips spend hooks; piggy Full/×2/Early incl. the expired race; stale-Pending
resume; limits flag-not-block) · `GooglePlayReceiptVerifierTests` over a fake gateway (state 0/1/2, purchaseType, package
mismatch) · `AppleJwsTests` (sample JWS; bundle/env/product mismatch each rejected) · `FakeVerifierProductionGuardTests` ·
`WebhookTests` (idempotent on event id; refund → policy; renewal → window appended) · one `[Trait("Category","Smoke")]` Fake
redeem round-trip against a local server.

## 8. Client (Khela.Play, `Assets/1Khela/Scripts/Store/`, `namespace PlayCard.Store`)

**Package:** `com.unity.purchasing` **5.2.0** (the WGWB-proven build on this Editor version + the R8 incident doc; pulls
`com.unity.services.core`); upgrade to 5.3.x later is a manifest line. Enable Purchasing in the Services window (flips
`UnityConnectSettings.UnityPurchasingSettings.m_Enabled`, currently 0); add `Assets/Resources/BillingMode.json` =
`{"androidStore":"GooglePlay"}` (WGWB's file); iOS min version **15** (StoreKit 2). `link.xml` needs nothing (IAP is
`[Preserve]`; DTOs covered by the existing `Khela.Common` + `Assembly-CSharp` preserve-all). WebGL: Unity IAP unsupported →
`StorePlatform.Web` later; the flow is unchanged.

| File | Role |
|---|---|
| `StorePlatformResolver.cs` (static) | `StorePlatform Current`: Editor → `Fake`; Android → `GooglePlay` (Amazon behind a future define + BillingMode); iOS/tvOS/macOS → `AppStore`; WebGL → `Web` |
| `StoreCatalog.cs` (plain singleton, the `PiggyState` shape) | `Current`, `Changed`, `RefreshAsync(force)`, `TryGet(productId)`, `ProductsForThisPlatform`; last catalog cached to `persistentDataPath/store_catalog.json` so Unity IAP can initialise before the API answers; 60 s freshness |
| `IapService.cs` (MonoBehaviour, `[RuntimeInitializeOnLoadMethod]` self-bootstrap like `KhelaAuthService`, `DontDestroyOnLoad`; inspector knobs: `initializeOnLogin`, `useFakeStoreInEditor`, `fakeStoreUIMode`, `fetchPurchasesOnReady`, `redeemRetries/backoff`, `verboseLog`) | **Same public surface as WGWB's `IAPService`** (`State`, `IsReady`, `TryPurchase(productId)`, `GetLocalizedPriceString/Title/Description`, `IsProcessing`, `HasFetchedProduct`, `IsProductExplicitlyUnavailable`, `RefreshExistingPurchases`, `RestoreTransactions`, events `OnInitializationStateChanged / OnCatalogUpdated / OnProcessingStateChanged / OnPurchaseCompleted`). Differences: waits for `StoreCatalog` (not `ShopService`); `ProductDefinition(id, StoreIdFor(platform), type)` from the catalog; `FetchProducts(defs, new MaximumNumberOfAttemptsRetryPolicy(3))` + reconnect on `Disconnected` / app resume / shop open (incident-doc hardening); `TryPurchase` → `/intent` first; **`ProcessPendingOrder` = redeem on the server, then confirm** (§5.2; no PlayerPrefs processed-tx list — the server is the idempotency); `SetObfuscatedAccountId(sha256(userId))` / `SetAppAccountToken(Guid userId)`; pending-order queue until `AccountManager.IsReady`; `KhelaAnalytics.LogPurchaseStarted/Completed` + new `LogPurchaseFailed/Redeemed` |
| `BlackjackRestClient` (additive) + `Game/Net/StoreModels.cs` | `GetStoreCatalogAsync(platform)`, `StoreIntentAsync`, `GetStorePurchasesAsync`, `RestorePurchasesAsync`, and **`RedeemPurchaseAsync` routed through `BalanceChangingAsync`** with `StoreRedeemResultData : IChipBalanceResult` — the house rule: `BalanceMaybeChanged` → `WalletManager` applies the chip hint instantly and reconciles Kash → every HUD repaints without callers remembering to refresh |
| `StorePurchaseButton.cs` | port of WGWB's `ShopIapPurchaseButton`: `[SerializeField] string productId`, title/price/amount/bonus `TMP_Text`s, loading/unavailable roots, `canInteract = IsReady && HasFetchedProduct && !Unavailable && !Processing`; price from the store, amount/badge from the catalog; `ButtonSound`/`ButtonJuice`/`Haptic` beside it. Reza places it on his cards — the Shop screen itself is his (`iap-shop-design`; current `Shop.unity` is the asset-kit mockup with no scripts) |
| `PiggyPurchaseBridge.cs` (beside `PiggyPanel` on `Piggy_Canvas.prefab`) | `PiggyPanel.BreakRequested(option)` → `TryPurchase(PiggyState.Current.SkuFor(option))`; failed/cancelled → `panel.CancelBreak()`; granted → `PiggyState.RefreshAsync(true)` → existing celebration (`PiggyBreakDirector`); `PiggyScreen.View.infoText` gets the localized price |
| `PassPurchaseBridge.cs` | `PassPanel.SubscribeRequested` → `TryPurchase("golden_pass")` → pass state refresh |
| `StoreSettings.asset` (optional SO, `Khela ▸ Store Settings`) | `fakeStoreUIMode`, `catalogCacheSeconds`, `redeemBackoffSeconds`, per-platform enable overrides (hide the store on WebGL), verbose logging |
| `Assets/Plugins/Android/proguard-user.txt` | insurance from `UNITY_IAP_R8_RELEASE_INCIDENT.md`: `-keep class com.android.billingclient.** { *; }`, `-keep interface com.android.billingclient.** { *; }`, `-keep class com.unity.purchasing.** { *; }`, `-keepclasseswithmembernames class * { native <methods>; }` + the GPGS rules WGWB carries. Khela has `AndroidMinifyRelease: 0` today, so nothing strips — but with minify on, this file + "Custom Proguard File" is the difference between a working and a dead store. Release check: `usage.txt` must not list `com.android.billingclient.api.BillingClient`; `mapping.txt` keeps the name |
| `SceneNavigator.Shop` + `GoToShop()` | when Reza's Shop screen exists |

Failure handling: network fails before redeem → order stays pending, re-driven by the next `FetchPurchases`; server
transient/5xx → backoff, keep pending; `Invalid` → confirm (stops the loop; server keeps the evidence); `Pending` → keep
pending, poll on resume; crash after redeem before confirm → re-delivery → `AlreadyGranted` → confirm; double tap → processing
lock + server intent; store not ready → button disabled with a reason (never silent — `PiggyPanel` already warns when nothing
listens). Editor loop: Unity Fake store dialog → redeem `Platform=Fake` → local server `Store:Fake:Enabled` → real ledger row +
HUD update. Device loop: Play **license tester** + a Play-installed internal-testing build (Play Billing does not work from a
sideloaded APK signed differently; `IsTest` purchases are free).

## 9. Platform enablement — what "enable" means

| Platform | Client | Server | Phase |
|---|---|---|---|
| Android / Google Play | build target Android; Unity IAP selects Google Play (`BillingMode.json`); store ids from the catalog | `Store:GooglePlay:Enabled` + `PackageName` + service-account JSON; `GooglePlayReceiptVerifier` | **1** |
| Editor / Windows dev | Unity Fake store | `Store:Fake:Enabled` (Development only) | **1** |
| iOS / macOS App Store | build target iOS (signing not configured yet); Unity IAP → Apple SK2; `StoreIds.AppStore` | `Store:AppStore:Enabled` + IssuerId/KeyId/.p8 + bundle id; `AppStoreReceiptVerifier` (written in 1, enabled in 3) | 3 |
| Web / WebGL / coinfolytics web store | no Unity IAP; hosted checkout client later | `Web` verifier = the "second feed" (Stripe/bKash/Nagad webhook → same `RedeemAsync`) | later |
| Amazon | `BillingMode.json androidStore=AmazonAppStore` + define; store id | Amazon RVS verifier | if ever |
| kill switch | — | `Store:Enabled` / per-platform `Enabled` on the Redis overlay, no deploy | — |

Recipes: **add a product** = catalog row (+ create the store product in Play Console / App Store Connect) → appears on any
`StorePurchaseButton` with that id. **Disable a product / platform / the store** = toggle. **Add a store vendor** = verifier
adapter + `StorePlatform` member. **Add a product effect** = grant handler.

## 10. Traps (each one cost somebody real money or a week)
- **Google auto-refunds purchases not acknowledged within 3 days.** If the client never confirms (crash, uninstall) the player keeps
  the chips *and* gets the money back. Server-side acknowledge on grant + the reconciler backstop close it.
- **Wallet `CorrelationId` is `varchar(64)`**; a Play purchase token is 150+ chars, Apple ids ~20. Keys are `iap:{guid:N}[:i]`; store
  ids live in `StorePurchases` columns. **Never re-key a spent id** (money-path-red-lines). `PlayerPassEntitlement.OriginalTransactionId`
  is 96 chars → Google token hashed there, raw token on `StorePurchases`.
- **Google `orderId` can be null** (some test/promo purchases) — the unique key is the **purchaseToken**, orderId is a column.
- **Confirming on a transient failure loses the purchase forever; not confirming a bad receipt re-delivers it forever.**
  `Granted / AlreadyGranted / Invalid` confirm; everything else waits.
- **`TransactionType.Purchase` means in-game spend.** Credits are `PaidPurchase`; a report summing `Purchase` as revenue is wrong.
- **Fake verifier must be impossible in Production** — gate on `IHostEnvironment`, not only config (the overlay SHADOWS appsettings).
- **The admin panel is local-only** → the catalog travels by seed file (Settings ▸ Transfer), or the VPS has an empty store.
- **Two DBs and two servers** — the device hits the VPS; migrations on both; `Khela.Common.dll` rebuilt + copied or the client won't
  see the DTOs.
- **Pending orders before auth** and **the 20 s REST timeout**: queue, bound, retry — never drop.
- **Play Billing needs a Play-installed, Play-signed build**; sideloaded APKs fail at `Connect`/`FetchProducts` with an empty catalog
  that looks like a code bug (incident doc's diagnosis matrix).
- **R8** only bites when minify is on (off today) — keep the rules file; the GPGS bridge has the same failure mode.
- **Apple**: StoreKit 2 → verify the JWS, not the legacy receipt; sandbox accounts are developer-created (accept, flag IsTest).
- **Piggy**: the window starts on *seen*, not on full; a purchase landing after expiry pays capacity (§5.2 g).
- **Catalog edits must not rewrite history**: fulfilment reads `CatalogSnapshotJson`, never the live document.

## 11. External setup (Reza; nothing here is code)
- **Play Console**: create the in-app products (ids = catalog ids; activate; price tiers) + the `golden_pass` subscription; **License
  testing** accounts; internal-testing track with a Play-installed build; Monetization setup ▸ RTDN topic + push subscription →
  `https://khela…/api/store/webhooks/google` (with the subscription phase). **Google Cloud**: enable the *Google Play Android Developer
  API*; a **dedicated Play-only service account** (least privilege — the Firebase `khela-786` admin key already owns auth; reuse
  is possible but mixes blast radius); Play Console ▸ Users & permissions ▸ invite the service-account email with *View financial
  data* + *Manage orders and subscriptions* (app-level). Quirk: 401/"insufficient permissions" for up to ~24 h after granting;
  editing and saving any product once usually unsticks it. Key file server-only, never in git.
- **App Store Connect** (phase 3): consumables + subscription group; Users & Access ▸ Integrations ▸ App Store Server API key
  (Issuer ID, Key ID, `.p8`); App Information ▸ Server Notifications V2 URLs (prod + sandbox) → `…/api/store/webhooks/apple`;
  sandbox testers; Paid Apps agreement; signing/team id in Player Settings.
- **VPS**: migration script, `Store` section in the server's `appsettings.json`, the service-account file, seed file with the catalog.
- **Unity**: install `com.unity.purchasing` 5.2.0; Services ▸ Purchasing on; `BillingMode.json`; iOS min 15.
- Store listing compliance (18+, "virtual chips only, no real money", disclose IAP, subscription terms, restore purchases) —
  `GROWTH_FIRST_10K.md`, `UI_SCREEN_INVENTORY.md` §1.

## 12. Build order (each step leaves `dotnet build` green and the tests passing; all in the MAIN tree)
1. **Server core** — `Khela.Common.Store` DTOs (+ DLL copy); `StoreCatalog` (doc, defaults, validate) + tests; models + migration
   (`StorePurchases`, `StoreEvents`, `PiggyBreaks` columns, drop `StoreItems`); `TransactionType.PaidPurchase`; `StoreConfig` /
   `StoreMath` + tests; verifier registry with **Fake** + **GooglePlay** (`Google.Apis.AndroidPublisher.v3`) + **AppStore**
   (library, disabled); grant handlers Lines + VipBooster + **PiggyBreak** (= `PiggyService.BreakAsync` + dev `/api/piggy/break` +
   tier SKUs + DTO fields) + GoldenPass; `StorePurchaseService` (intent/redeem/restore/history/redrive); `StoreController`; spend
   hooks (VIP / LP / XP); `StoreReconciliationService`; DI. Tests §7.3. (~2–3 days)
2. **Admin** — Khela.Web Store pages (catalog, purchases, platforms), Settings ▸ Store switches + Transfer group, Testing fake-redeem,
   dashboard tiles. (~1 day)
3. **Client** — package + BillingMode + Services toggle; `StorePlatformResolver`, `StoreCatalog`, `IapService`, REST methods +
   `StoreRedeemResultData`, `StorePurchaseButton`, `PiggyPurchaseBridge`, `PassPurchaseBridge`, analytics methods, proguard insurance;
   Editor loop against the local server with Fake. (~1–2 days) → Reza: Shop screen on the bright HUD language, cards carrying
   `StorePurchaseButton`s, `SceneNavigator.Shop`.
4. **Device** — Play Console products + license testers + internal build; end-to-end on a phone against the VPS: buy · kill the app
   between grant and confirm, relaunch → `AlreadyGranted` · cancel · pending · piggy break · test refund via Play → poller flags/rolls back.
5. **Ship switches** — VPS migration, `Store` config + service account + catalog seed, `Store:GooglePlay:Enabled` on.
6. **Subscription + webhooks** — RTDN + ASN v2 endpoints, renew/expire/revoke handling, Golden live.
7. Later lanes in any order: iOS build + `Store:AppStore:Enabled`; web feed; Amazon.

## 13. Choices made in the merge (= the defaults; flip any in the brainstorm)
| # | Topic | Merged choice | Why |
|---|---|---|---|
| M1 | Catalog storage | Redis doc `khela:store` + Khela.Web editor + `CatalogSnapshotJson` on the purchase row | reuses the pass/chests/daily editor + validator + seed-file machinery; no catalog migration |
| M2 | Ledger type | new `TransactionType.PaidPurchase = 6` | `Purchase` is already the cosmetics debit; revenue queries must not mix |
| M3 | Google idempotency key | `purchaseToken` (orderId = column) | orderId can be null on test/promo purchases |
| M4 | Unity IAP version | 5.2.0 | proven on Reza's device + the R8 incident doc; 5.3.x = a later manifest line |
| M5 | Piggy SKUs | 3 per tier × the tier ladder (4 default tiers → 12) | config; price scales with tier |
| M6 | Refunds | `Rollback` via `RollbackAsync`, fallback `Flag` | the house reversal primitive; never negative; deters refund abuse |
| M7 | Apple verifier | `AppStoreServerLibraryDotnet` wrapped; built in v1, enabled with the iOS build | port of Apple's official lib; owning x5c/OCSP correctness by hand buys nothing |
| M8 | v1 scope | `/intent` + reconciler (re-drive, ack backstop, voided poll) in v1; webhooks with the subscription | consumables don't need webhooks; the subscription does |
| M9 | Golden Pass v1 | sold as a true subscription product from day one; entitlement window = verified expiry; renewals via reconciler refresh until webhooks land | no re-listing later; `GrantGoldenAsync` already models windows |
| M10 | Client REST | additive methods on `BlackjackRestClient`; redeem via `BalanceChangingAsync` | the house HUD-repaint rule |
| M11 | Naming | `Store:*` config, `StorePurchases`, `StorePlatform`, `Services/Store/`, `PlayCard.Store`; client `IapService` keeps WGWB's name | one vocabulary; "IAP" = the store SDK wrapper only |
| M12 | Service account | dedicated Play-only | least privilege for a money credential |
| M13 | Test purchases | grant normally, `IsTest`, excluded from spend hooks + revenue | license testers test the real path |
| M14 | Button display amount | server catalog (lines, bonus %) + the store's localized price | the client never holds grant truth |
| M15 | XP on purchase | hook exists, `Store:XpPerUsd = 0` | purchased chips accrue XP when wagered (PROGRESSION_SPEC) |

## 14. Brainstorm list (open questions, none blocking slice 1)
**Settled 2026-08-22 (→ §3.1):** store minimums 5M/$1.99 · 10M/$2.99; ladder ≤ 5M/$ at the anchor; piggy 10M/$1.99 + 20M/$3.99
(~3-day fill, per-tier rate); Kash 100/$1.99; exchange 1M:1 one-way. **Open:** free-flow sizing vs the new ladder (daily login /
free pass / missions / level-ups — chips AND the Kash lines in chests/pass, which are priced by the Kash lane now) · upper chip
rungs + bonus % · starter pack / first-purchase ×2 · daily special cadence · what Kash merchandises · refund policy + refund-abuse
thresholds (purchase velocity caps, block after N refunds) · Golden v1 shape (M9) · test-purchase handling on the live VPS ·
where Buy lives (Shop scene + inline offers: out-of-chips upsell at the 1M tables, piggy, pass, VIP) · tournament prizes =
chips/cosmetics only (no Kash for chip buy-ins) · VIP "more chips per dollar" (PROGRESSION: tier-gated bonus) · regional pricing
tiers · launch order (Android → iOS → web) · web store / bKash-Nagad feed · compliance set (18+, disclaimers, restore purchases,
subscription terms sheet) · analytics funnel events · localisation of titles · store-listing IAP disclosure.

*Related: `PIGGY_BANK_SPEC.md` §4/§6 (step 4 = this), `PASS_SPEC.md` §5.4 (the IAP hop), `PROGRESSION_SPEC.md` (rail-agnostic
spend hooks; never odds), `DECISIONS.md` E-01..E-10, `PROJECT_PLAN.md` Stage A, `CONFIG_TRANSFER.md`, memory `iap-shop-design`,
`money-path-red-lines`, `khela-deployment-server`, `client-networking`.*
