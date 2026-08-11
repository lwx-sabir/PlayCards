# Pass Spec — Free Daily Pass + Golden (paid) Pass

*Living build checklist. Nothing here may weaken a NON-NEGOTIABLE rule in `CLAUDE.md` —
in particular the dual-currency guardrail (§2) and wallet integrity (§3). `dotnet build`
must pass with no unexpected pending migration.*

Status: **slice 1 of 7 built** (the reward seam, §2 — `RewardKind`/`RewardGrant`, the three granters,
`RewardGrantService`, the XP cap bypass, and the `PlayerRewards` Kind/ItemId migration; 24 tests green).
The pass itself (§3–§7) is not built yet.

---

## 1. What we're building

One **season-long daily ladder with two tracks**:

```
Day →      1      2      3      4      5   …   30
FREE     [ ✓ ]  [ ✓ ]  [ ✓ ]  [   ]  [   ]     [★]     ← everyone
GOLDEN   [ 🔒]  [ 🔒]  [ 🔒]  [   ]  [   ]     [★]     ← needs an active entitlement
```

- A **season** is a period (default: one calendar month) with an ordered ladder of **nodes**.
- Each node carries **two reward sets**: `Free` (always) and `Golden` (only with entitlement).
- A player advances **one node per UTC day**, by claiming. `Node = (claims this season) + 1`.
  Missing a day does not burn a node — it just means you may not reach the end of the ladder.
  This is deliberately forgiving: it drives daily return without punishing a bad week.
- **Buying Golden is retroactive.** Every golden reward for nodes already claimed is enqueued
  into the existing reward inbox (`PlayerRewards`, `RewardSource.Pass`). That retro pile *is*
  the conversion lever — the player sees exactly what they're owed before paying.
- Golden is **per season** (`ExpiresAt` defaults to season end). The row already carries a
  nullable `ExpiresAt`, so a rolling monthly subscription later is a value change, not a schema
  change. `MARKETING_SPEC` A5 (R1 renewal defence) reads its signals off this system:
  entitlement rows give renewal dates, claim rows give the "subscriber not claiming dailies"
  non-renewal predictor.

**Non-goals for v1:** pass points from wagering (the axis is days, not points), streak
multipliers, gifting a pass, multiple concurrent passes, weekly mini-passes. Every one of them
is additive on the model below.

---

## 2. The extensibility seam (build this FIRST)

The user requirement is: *ship Chips / Kash / XP now, be able to hand out lottery tickets,
clothes, other currencies later without touching the pass.* So the pass must never speak
`CurrencyType` — it speaks **reward lines**.

### 2.1 The reward line — `Khela.Common.Rewards`

```csharp
/// <summary>What a reward line pays out. APPEND ONLY — persisted as int in PlayerRewards.</summary>
public enum RewardKind
{
    Currency = 0,   // wallet currency        Id = "Chips" | "Coins" | "Gems" | "Kash"
    Xp       = 1,   // progression XP         Id = null
    Chest    = 2,   // roll a chest           Id = "CK_Chest:Rare"   (key:tier)
    Cosmetic = 3,   // avatar SKU grant       Id = CosmeticSku.SkuId      [LATER]
    Item     = 4,   // generic inventory item Id = item key, e.g. "lottery_ticket"  [LATER]
}

/// <summary>One line of a reward payload. JSON-friendly (admin editor round-trips it).</summary>
public sealed class RewardGrant
{
    public RewardKind Kind { get; set; }
    public string Id { get; set; }        // meaning depends on Kind (see above)
    public decimal Amount { get; set; }   // amount / count; 1 for a unique item
}
```

### 2.2 The dispatcher — `Services/Rewards/RewardGrantService`

```csharp
public interface IRewardGranter
{
    RewardKind Kind { get; }
    /// <summary>Grant ONE line. MUST be idempotent on idemKey. Returns what was actually applied
    /// (the applied value, not the requested one) for the client's reward animation.</summary>
    Task<GrantedLineDto> GrantAsync(Guid userId, RewardGrant line, string idemKey, string description);
}

public interface IRewardGrantService
{
    /// <summary>Grant a payload line-by-line. Per-line idemKey = $"{idemKey}:{index}".
    /// An unknown/unregistered kind is SKIPPED and logged — never throws, never blocks the rest.</summary>
    Task<List<GrantedLineDto>> GrantAllAsync(Guid userId, IReadOnlyList<RewardGrant> lines, string idemKey, string description);
}
```

Registered as `IEnumerable<IRewardGranter>` → keyed dictionary in DI.

| Granter | v1 | Routes to |
|---|---|---|
| `CurrencyGranter` | ✅ | `IWalletService.CreditAsync(..., TransactionType.Bonus, idemKey)` |
| `XpGranter` | ✅ | `IProgressionService.GrantXpAsync(userId, amount, "pass", idemKey, bypassDailyCap: true)` |
| `ChestGranter` | ✅ | `IChestService.OpenAsync(userId, key, tier, idemKey)` (already deterministic per key) |
| `CosmeticGranter` | later | `UserCosmetics` row, `Source = "grant"`, unique (user, sku) |
| `ItemGranter` | later | a new `PlayerItems` table (out of scope here) |

**Adding "lottery tickets" later = one enum value + one `IRewardGranter` class.** No pass code,
no schema change, no client change beyond an icon.

### 2.3 Guardrails on the seam

- **Currency allowlist, fail-closed.** Reuse the chest precedent verbatim:
  `{ Chips, Coins, Gems, Kash }`. `Tokens`, an undefined int, and any *future* appended
  currency are rejected — enforced **twice**: at admin save (`PassCatalog.Validate`) and again
  inside `CurrencyGranter` (so a raw Redis write can never credit them). CLAUDE.md §2/§4.
- **Pass XP EXCEEDS the daily XP cap** (decision). `IProgressionService.GrantXpAsync` gains an
  optional `bool bypassDailyCap = false`; `ApplyXpAsync` skips the cap clamp when set, keeping
  the same idempotency + auto-level-up + level-reward path. Existing callers are unchanged by
  the default. Safe because pass XP isn't farmable: it's server-authored config, gated to one
  claim per UTC day, and a paid track on top. Only the pass passes `true` — round accrual,
  gifts and other flat grants stay capped.
- **Every granter is idempotent on its key or it doesn't ship.** No exceptions.

### 2.4 One migration on the existing inbox

`PlayerReward` today is `(Currency, Amount)` only, so it can't hold a cosmetic or a ticket.
Migration `AddRewardKindToPlayerRewards`:

```csharp
public RewardKind Kind { get; set; } = RewardKind.Currency;   // int, default 0 → existing rows unchanged
[MaxLength(64)] public string ItemId { get; set; }            // null for Currency/Xp
```

`RewardService.ClaimAsync` then routes through `IRewardGrantService` instead of calling the
wallet directly. Existing callers (level-up, milestone) keep working — `Kind` defaults to
`Currency` and `Currency`/`Amount` stay the payload for that kind.

---

## 3. Config — `khela:pass` (Redis overlay + code defaults)

Same shape as `khela:missions` / `khela:chests`: admin-editable JSON in Redis, code defaults
when absent or unparseable, read per call so a dashboard save applies with no redeploy.

```csharp
public sealed class PassNode
{
    public int Index { get; set; }                 // 1-based day/step
    public bool IsMilestone { get; set; }          // UI emphasis only
    public List<RewardGrant> Free { get; set; } = new();
    public List<RewardGrant> Golden { get; set; } = new();
}

public sealed class PassSeason
{
    public string Key { get; set; }                // stable id, e.g. "2026-09" — claims reference this forever
    public string Title { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }           // exclusive
    // Golden is sold for REAL MONEY only — never for an in-game currency. These are the store
    // product ids; the displayed price comes from the platform store at runtime (localized),
    // with GoldenPriceUsd as the offline/fallback label only.
    public string GoldenProductIdApple { get; set; }   // e.g. "khela.pass.golden.monthly"
    public string GoldenProductIdGoogle { get; set; }
    public decimal GoldenPriceUsd { get; set; }        // display fallback, NOT a charge
    public List<PassNode> Nodes { get; set; } = new();
}

public sealed class PassConfig
{
    public bool Enabled { get; set; } = true;
    public List<PassSeason> Seasons { get; set; } = new();
    public PassSeason Current(DateTime nowUtc) =>
        Seasons?.FirstOrDefault(s => nowUtc >= s.StartUtc && nowUtc < s.EndUtc);
}
```

`PassCatalog` mirrors `ChestCatalog`: `RedisKey = "khela:pass"`, `JsonOptions` (indented +
`JsonStringEnumConverter`), `Defaults()`, `TryParse()`, `Validate()`.

**`Validate()` rejects:** overlapping seasons; `EndUtc <= StartUtc`; duplicate/​non-contiguous
node indexes; empty node payloads on *both* tracks; a currency line outside the allowlist
(with the explicit "tradeable token" message); negative amounts; a `Chest` line whose
`key:tier` isn't in the chest catalog; a season with a golden payload but no store product id.

**No current season, or `Enabled = false` → the pass is OFF** (endpoint returns
`Active = false`, claim is rejected). Fail-closed.

### 3.1 Starter ladder (30 nodes, tune in the dashboard)

Anchored against what already exists: daily missions pay 500–4,000 chips/day, the common
chest rolls 2k–8k chips + 5–20 Kash.

| Node | Free | Golden (additional) |
|---|---|---|
| 1–5 | 1,000 chips · 50 XP | 3,000 chips · 5 Kash · 100 XP |
| 6–9 | 1,500 chips · 75 XP | 5,000 chips · 10 Kash · 150 XP |
| **10** ★ | 3,000 chips · 10 Kash · 150 XP | `CK_Chest:Uncommon` · 25 Kash · 300 XP |
| 11–19 | 2,000 chips · 100 XP | 7,500 chips · 15 Kash · 200 XP |
| **20** ★ | 5,000 chips · 20 Kash · 250 XP | `CK_Chest:Rare` · 50 Kash · 500 XP |
| 21–29 | 2,500 chips · 125 XP | 10,000 chips · 20 Kash · 250 XP |
| **30** ★ | 10,000 chips · 30 Kash · 500 XP | `CK_Chest:Rare` · 150 Kash · 1,000 XP |

Rough season totals: free ≈ 65k chips + 60 Kash; golden adds ≈ 200k chips + 500 Kash + 2 chests.
Golden price: **real money, ~$4.99/month** (store product, §5.3). Sanity check the Kash ratio
against the cosmetics price list before launch.

These numbers are only the seed `Defaults()` — the real ladder is authored in the admin panel
(§6.1), which is where who-gets-what-and-how-much is decided.

### 3.2 Config backups (all admin-edited JSON overlays)

The overlays (`khela:pass`, `khela:missions`, `khela:chests`, `khela:settings`) live in Redis,
which is not a backup. A hosted `ConfigBackupService` in Khela.Game:

- Runs **every 3 days** (and once at startup), reading each registered overlay key.
- Writes `App_Data/config-backups/{key}/{yyyyMMdd-HHmmss}.json` — **only if the content hash
  differs from the newest existing file**, so an untouched config doesn't accumulate copies.
- **No automatic deletion.** Old snapshots are pruned by hand once newer ones look good
  (they're small JSON; this is the explicit decision).
- Interval + directory are config knobs (`Config:BackupDays`, `Config:BackupDir`).
- Restore is manual and reviewed: the admin page (§6.1) lists snapshots with timestamp + size,
  offers **download** and **restore** (restore = load into the editor, re-`Validate`, then save
  — never a blind write straight to Redis).

---

## 4. Schema (one migration: `AddPassTables`)

Two tables. There is deliberately **no progress/summary table** — the claim ledger is the only
truth, so there's no cache to drift.

```csharp
[Table("PlayerPassClaims")]
[Index(nameof(UserId), nameof(SeasonKey), nameof(Node), IsUnique = true)]        // one node, once
[Index(nameof(UserId), nameof(SeasonKey), nameof(ClaimDateUtc), IsUnique = true)] // one claim per UTC day
public class PlayerPassClaim
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public Guid UserId { get; set; }
    [Required, MaxLength(32)] public string SeasonKey { get; set; }
    [Required] public int Node { get; set; }
    [Required] public DateTime ClaimDateUtc { get; set; }        // date only (UTC)
    [Required] public bool FreeGranted { get; set; }
    [Required] public bool GoldenGranted { get; set; }           // false if not entitled at claim time → retro-owed
    [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
}

[Table("PlayerPassEntitlements")]
[Index(nameof(UserId), nameof(SeasonKey), IsUnique = true)]
public class PlayerPassEntitlement
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public Guid UserId { get; set; }
    [Required, MaxLength(32)] public string SeasonKey { get; set; }
    [Required, MaxLength(32)] public string Source { get; set; }   // "iap" | "store" | "admin" | "gift"
    [MaxLength(96)] public string PurchaseRef { get; set; }        // wallet CorrelationId / IAP transaction id
    [Required] public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }                       // null = season end; set for subscriptions
    [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
}
```

The two unique indexes make double-claiming **structurally impossible**: a same-day retry
collides on `ClaimDateUtc`, a same-node race collides on `Node`.

---

## 5. Service — `Services/Pass/PassService`

```csharp
public interface IPassService
{
    Task<PassStateDto> GetStateAsync(Guid userId);
    Task<PassClaimResultDto> ClaimAsync(Guid userId);
    /// <summary>THE entitlement seam. Grants the entitlement + retro-enqueues owed golden rewards.
    /// Idempotent on (userId, seasonKey). Callers: IAP receipt validation (source "iap") and the
    /// admin panel (source "admin"). There is NO in-game-currency purchase path.</summary>
    Task<PassPurchaseResultDto> GrantGoldenAsync(Guid userId, string seasonKey, string source, string purchaseRef);
}
```

### 5.1 Claim (money-safe order — reserve, grant, complete)

1. Resolve `season = cfg.Current(utcNow)`; off/absent → `Fail("No active pass.")`.
2. `today = utcNow.Date`. Insert `PlayerPassClaim { Node = existingClaims + 1, ClaimDateUtc = today }`
   with `FreeGranted = false`. **A unique-index violation here means already claimed today** →
   reload the row and continue (idempotent replay), don't fail.
3. Node beyond the ladder → delete nothing, return `Fail("Season complete.")` *before* step 2.
4. Grant `node.Free` via `IRewardGrantService`, idemKey `pass:{season}:{user:N}:{node}:free`.
   Set `FreeGranted = true`.
5. If entitled (§5.2): grant `node.Golden`, idemKey `…:{node}:golden`, set `GoldenGranted = true`.
6. `CompletedAt = utcNow`, save.

If the process dies between 2 and 6 the row stays incomplete; the **next** claim call (same day)
hits the unique index, reloads the row, and re-runs the ungranted parts — each granter is
idempotent, so nothing is lost and nothing pays twice. Same pattern as `RewardService`.

### 5.2 Entitlement check

`IsGolden(userId, season)` = an entitlement row for (user, seasonKey) with
`ExpiresAt == null || ExpiresAt > utcNow`. Server-side only. The client's `IsGolden` flag is
display; it never gates a grant.

### 5.3 Purchase (real money) + retro-grant

**Golden is sold for real money only.** The client buys the store product, the server validates
the receipt, and validation calls `GrantGoldenAsync`. No chips/Kash/Gems path exists — an
in-game-currency purchase is not a thing the API can express.

```
client → platform store purchase → POST /api/pass/purchase { platform, productId, receipt }
      → IIapService.ValidateAsync(...)                     ← Phase-0 IAP work, shared with chip packs
      → PassService.GrantGoldenAsync(user, season, "iap", transactionId)
```

Rules for the IAP hop (these belong to the IAP spec but the pass depends on them):
`transactionId` is the idempotency key end-to-end — a replayed receipt re-grants nothing;
validation is **server-side against Apple/Google**, never a client claim; an unvalidated or
mismatched product id grants nothing. A refund/chargeback webhook revokes the entitlement row
(`ExpiresAt = now`); already-collected rewards are NOT clawed back — the ledger stays append-only.

**Dependency:** the paid track cannot go live before IAP receipt validation ships. Until then
the free track runs standalone and the admin grant (§6.2) covers QA/comps — the ladder, the
retro machinery and the entitlement table are all exercised by that path, so nothing about
Golden is untested when IAP lands.

`GrantGoldenAsync`:
1. Upsert the entitlement row (unique (user, season) → a concurrent retry is a no-op).
2. For every existing claim with `GoldenGranted = false`: **enqueue** each golden line into the
   inbox via `IRewardService.GrantAsync(..., RewardSource.Pass, idemKey "pass-retro:{season}:{user:N}:{node}:{i}")`,
   then set `GoldenGranted = true`.
   Enqueued, **not** auto-credited — chips arrive only when the player taps collect
   (the existing inbox rule), which also gives the purchase a satisfying payoff moment.
3. Future claims grant golden inline (§5.1 step 5).

### 5.4 Reads

`GetStateAsync` returns the whole ladder + per-node claim state in one call (30 nodes is small):

```
Active, SeasonKey, Title, StartUtc, EndUtc, NextResetUtc (tomorrow 00:00 UTC),
IsGolden, GoldenProductId (per platform) + GoldenPriceUsd fallback, CurrentNode, ClaimableToday,
RetroOwedNodes (count — the buy-CTA number),
Nodes[] { Index, IsMilestone, Free[], Golden[], Claimed, GoldenClaimed }
```

`RewardGrant` lines go to the client as-is (Kind + Id + Amount) so the client renders any future
kind generically — unknown kind → generic icon + label, never a crash.

---

## 6. API — `Controllers/PassController` (`[Authorize]`)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/pass` | full state (§5.4) |
| POST | `/api/pass/claim` | claim today's node; returns granted lines + new balances |
| POST | `/api/pass/purchase` | `{ platform, productId, receipt }` → validate → `GrantGoldenAsync("iap")` |

## 6.1 Admin panel — `/Pass` (Khela.Web, full CRUD page)

Not a JSON textarea. This page is where the whole economy of the pass is decided, so it gets
the same treatment as the Chests editor but bigger. Cookie-Identity admin gate as elsewhere;
saves go through `PassCatalog.Validate` and write the `khela:pass` overlay atomically.

**Seasons list** — table of every season (Key, Title, window, node count, Live/Upcoming/Ended,
golden product id). Actions: **Create**, **Clone** (copy a whole ladder into next month's dates
— the normal way to author a season), **Edit**, **Delete** (blocked once any claim row exists
for that key; offer "end early" instead — history must stay readable), **Enable/Disable**.

**Season editor** — window + title + store product ids + display price, then the **node grid**:

| Node | ★ | Free rewards | Golden rewards |
|---|---|---|---|
| 1 | ☐ | `Chips 1000` `XP 50` `+` | `Chips 3000` `Kash 5` `XP 100` `+` |

- A reward line is edited inline: **Kind** dropdown → the `Id` control swaps to match
  (Currency → enum dropdown, allowlist only; Chest → chest picker from `khela:chests`;
  Xp → no id; Cosmetic/Item → picker/key box, appear automatically as those granters ship) →
  **Amount**. Same `RewardGrant` JSON underneath, so the editor needs no change per new kind
  beyond the id control.
- Bulk tools, because 30 nodes is tedious: **add/remove nodes**, **apply a payload to a node
  range**, **copy Free → Golden ×N multiplier**, **duplicate node**, drag to reorder (renumbers).
- Live totals panel: season sum per currency + XP, free vs golden, so the payout is visible
  while authoring rather than discovered later.
- **Validate** button (dry-run, shows every error) and a **Preview as player** render of the
  ladder. Save is refused on any validation error.
- **Backups panel** (§3.2): snapshot list with timestamp/size, **download**, **restore into
  editor**. Restore never writes Redis directly — it loads, re-validates, and you save.

## 6.2 Admin — per-player

| Method | Route | Notes |
|---|---|---|
| POST | `/Players/{id}/grant-pass` | `GrantGoldenAsync(source: "admin")` — comp / support / QA |
| POST | `/Players/{id}/revoke-pass` | sets `ExpiresAt = now`; collected rewards are not clawed back |

The player detail page also shows current season node, claim history, and golden state.

---

## 7. Client (Unity, `PlayCard.Pass`) — after the server is green

- `PassScreen`: horizontal ladder, two rows (Free above, Golden below, locked rows dimmed with
  the price CTA), milestone nodes visually larger, "Day N of 30 · resets in HH:MM".
- Reward chips render from `RewardGrant` via a `RewardIconResolver` (kind + id → sprite + label),
  so a new kind is an art entry, not a code change.
- Claim → play the grant animation off the **server's returned applied amounts**, never local
  config values.
- Entry points: Home badge when `ClaimableToday`, and the reward-inbox badge for retro grants.

---

## 8. Build order

| # | Slice | Ships |
|---|---|---|
| 1 ✅ | Reward seam | `RewardKind`/`RewardGrant`, 3 granters, `RewardGrantService`, `GrantXpAsync(bypassDailyCap)`, `AddRewardKindToPlayerRewards` |
| 2 | Catalog | `PassCatalog` + `Defaults()` + `Validate()` + unit tests (no DB, no HTTP) |
| 3 | Core | `AddPassTables`, `PassService` (state + claim), `PassController` GET/claim |
| 4 | Entitlement | `GrantGoldenAsync` + retro-enqueue + admin grant/revoke (exercisable without IAP) |
| 5 | Admin | `/Pass` full CRUD page (§6.1) + `ConfigBackupService` (§3.2) |
| 6 | Client | `PassScreen` |
| 7 | Real money | `POST /api/pass/purchase` wired to IAP receipt validation once that ships |
| — | later | `CosmeticGranter`, `ItemGranter` + `PlayerItems` |

Slices 1–2 are pure and testable with no infrastructure; do them first. Slice 7 is gated on the
Phase-0 IAP work — everything before it ships and is playable on the free track.

## 9. Tests (xUnit, alongside `ChestCatalogTests`)

- `Validate` rejects: `Tokens` line, undefined currency int, overlapping seasons, duplicate node
  index, negative amount, unknown chest key, golden payload with no store product id.
- `CurrencyGranter` drops a non-allowlisted line even when `Validate` was bypassed (fail-closed).
- Node math: claims=0 → node 1; claims=29 → node 30; claims=30 → season complete.
- Retro set: 12 claims with `GoldenGranted=false` → exactly 12 enqueues, and a second
  `GrantGoldenAsync` enqueues 0.
- Replay: two `ClaimAsync` calls on the same UTC day → one node, one payout.
- XP: a pass grant lands in full with the daily cap already exhausted; a *round* accrual with
  the same profile is still clamped (the bypass is opt-in and doesn't leak).
- Backup: unchanged config → no second file; changed config → new file; neither ever deletes.

## 10. Definition of done

1. `dotnet build` passes; migrations added deliberately; snapshot in sync.
2. No NON-NEGOTIABLE weakened — `Tokens` unreachable on every path, wagerability untouched.
3. Every grant idempotent on a stable key; the client is told the *applied* amounts.
4. Pass off (no season / `Enabled=false` / bad config) pays nothing, breaks nothing.
