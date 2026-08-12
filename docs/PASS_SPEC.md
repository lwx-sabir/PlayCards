# Pass Spec — Free Daily Pass + Golden (paid) Pass

*Living build checklist. Nothing here may weaken a NON-NEGOTIABLE rule in `CLAUDE.md` —
in particular the dual-currency guardrail (§2) and wallet integrity (§3). `dotnet build`
must pass with no unexpected pending migration.*

Status: **slices 1–2 of 7 built.** §2 reward seam (`RewardKind`/`RewardGrant`, three granters,
`RewardGrantService`, the XP cap bypass, `PlayerRewards.Kind/ItemId` — migration applied) and §3
catalog (`PassCatalog`: programs, monthly cycle resolution, catch-up rules, validate, totals).
64 tests green, all pure. Next: §4/§5 tables + `PassService` + `PassController`.

---

## 1. What we're building

The **Monthly Pass**: a daily ladder with two tracks, running one calendar month, renewing
automatically on the **1st at 00:00 UTC**.

```
Day of month →    1      2      3      4      5   …   28
FREE            [ ✓ ]  [ ✓ ]  [ ✓ ]  [   ]  [   ]    [★]     ← everyone, today's node only
GOLDEN          [ 🔒]  [ 🔒]  [ 🔒]  [   ]  [   ]    [★]     ← monthly subscription; unlocks retroactively
```

- **Node index = day of the cycle.** Day 7 of the month is node 7. You cannot outrun the
  calendar, and the ladder is not a treadmill you fall off — it just tracks the date.
- Each node carries **two reward sets**: `Free` (always) and `Golden` (subscribers only).
- **A cycle is one calendar month**, generated automatically — no authoring per month. The same
  authored ladder repeats every cycle until you edit it, with optional per-month overrides
  (§3) for a themed December, Ramadan, etc.
- **Golden is a monthly auto-renewing IAP subscription.** Subscribe on any day of the month;
  the entitlement window comes from the store, so it runs (say) the 20th → the 20th, spanning
  two cycles. The *cycle* renews on the 1st; the *subscription* renews on its own store date.
  These are deliberately independent.
- **Subscribing unlocks the days you missed.** Every node up to today's index gets its golden
  payload enqueued into the reward inbox (`PlayerRewards`, `RewardSource.Pass`) the moment the
  receipt validates. That retro pile *is* the conversion lever — the player sees what they're
  owed before paying. **T&C:** current cycle only, must be collected before the cycle ends, and
  it's void if the purchase is refunded (§5.5).
- **Catch-up is a paid privilege by default** (`CatchUp = GoldenOnly`): free players get *today's*
  node only, so missing a day really does cost the free reward — that's what makes daily return
  worth something and what makes the subscription worth buying. One config knob flips it to
  `None` (nobody backfills) or `All` (everyone backfills). §3.
- `MARKETING_SPEC` A5 (R1 renewal defence) reads its signals off this system: entitlement rows
  give renewal dates, claim rows give the "subscriber not claiming dailies" non-renewal predictor.

**A Season Pass is a SEPARATE, LATER program** — not one month, its own ladder, its own product,
probably its own progression axis (points from play rather than days). This spec builds the
monthly pass but the model is keyed by **pass program** throughout (`PassKey` on every row), so
adding a season pass later is a new program row, not a schema migration. Do not conflate the two.

**Non-goals for v1:** pass points from wagering, streak multipliers, gifting a pass, running two
programs at once, weekly mini-passes. Each is additive on the model below.

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
public enum PassCadence
{
    Monthly = 0,   // cycles generate themselves: 1st 00:00 UTC → 1st next month. CycleKey "yyyy-MM".
    Fixed   = 1,   // one explicit window (StartUtc/EndUtc) — what a future Season Pass uses.
}

/// <summary>Who may claim a node whose day has already passed.</summary>
public enum CatchUpPolicy
{
    None       = 0,   // today's node only, both tracks
    GoldenOnly = 1,   // DEFAULT — free: today only; golden: every earlier node unlocks
    All        = 2,   // both tracks backfill the whole cycle
}

public sealed class PassNode
{
    public int Index { get; set; }                 // = day of the cycle (day 7 → node 7)
    public bool IsMilestone { get; set; }          // UI emphasis only
    public List<RewardGrant> Free { get; set; } = new();
    public List<RewardGrant> Golden { get; set; } = new();
}

/// <summary>A month-specific ladder that replaces the recurring one for exactly one cycle.</summary>
public sealed class PassCycleOverride
{
    public string CycleKey { get; set; }           // "2026-12"
    public string Title { get; set; }
    public List<PassNode> Nodes { get; set; } = new();
}

/// <summary>A pass PROGRAM. "monthly" is the one this spec builds; a Season Pass is another row.</summary>
public sealed class PassProgram
{
    public string Key { get; set; }                // stable id, e.g. "monthly" — every claim row references it
    public string Title { get; set; }
    public bool Enabled { get; set; } = true;
    public PassCadence Cadence { get; set; } = PassCadence.Monthly;
    public CatchUpPolicy CatchUp { get; set; } = CatchUpPolicy.GoldenOnly;

    public DateTime? StartUtc { get; set; }        // Fixed cadence only
    public DateTime? EndUtc { get; set; }          // Fixed cadence only, exclusive

    // Golden is sold for REAL MONEY only — never for an in-game currency. These are the store
    // subscription product ids; the displayed price comes from the platform store at runtime
    // (localized), with GoldenPriceUsd as the offline/fallback label only.
    public string GoldenProductIdApple { get; set; }   // e.g. "khela.pass.golden.monthly"
    public string GoldenProductIdGoogle { get; set; }
    public decimal GoldenPriceUsd { get; set; }        // display fallback, NOT a charge

    public List<PassNode> Nodes { get; set; } = new();                     // the RECURRING ladder
    public List<PassCycleOverride> CycleOverrides { get; set; } = new();   // per-month exceptions
}

public sealed class PassConfig
{
    public bool Enabled { get; set; } = true;
    public List<PassProgram> Programs { get; set; } = new();
}
```

**Cycle resolution** (`PassCatalog.CurrentCycle(program, nowUtc)`) returns
`{ CycleKey, StartUtc, EndUtc, Nodes, Length }`:

- `Monthly` → the calendar month containing `nowUtc`; key `"yyyy-MM"`; `Nodes` = the override for
  that key if one exists, else the recurring ladder.
- `Fixed` → the program's own window, key = the program key; null outside it.
- **Effective length = `min(ladder nodes, days in the cycle)`.** A 31-node ladder in April simply
  stops at 30; in February it stops at 28. Author the final milestone at **node 28 or lower** or
  February players can never reach it — `Validate()` warns on a milestone above 28.

`PassCatalog` mirrors `ChestCatalog`: `RedisKey = "khela:pass"`, `JsonOptions` (indented +
`JsonStringEnumConverter`), `Defaults()`, `ToJson()`, `TryParse()`, `Validate()`, `Totals()`.

**`Validate()` rejects:** duplicate program keys; a key longer than the 32-char column;
duplicate/​non-contiguous node indexes (the ladder must be `1..N`); a ladder longer than 31 for a
monthly program; empty node payloads on *both* tracks; a currency line outside the allowlist
(with the explicit "tradeable token" message); non-positive amounts; a `Chest` line whose
`key:tier` isn't in the chest catalog; a reward kind this build can't pay; a program with a golden
payload but no store product id; a `Fixed` program without a valid window; a `CycleOverride` whose
key isn't a real cycle key or whose ladder breaks the same rules.

**No enabled program, no current cycle, or `Enabled = false` → the pass is OFF** (endpoint returns
`Active = false`, claim is rejected). Fail-closed.

### 3.1 Starter ladder (28 required + 3 bonus nodes, tune in the dashboard)

Anchored against what already exists: daily missions pay 500–4,000 chips/day, the common
chest rolls 2k–8k chips + 5–20 Kash.

| Node | Free | Golden (additional) |
|---|---|---|
| 1–5 | 1,000 chips · 50 XP | 3,000 chips · 5 Kash · 100 XP |
| 6–9 | 1,500 chips · 75 XP | 5,000 chips · 10 Kash · 150 XP |
| **10** ★ | 3,000 chips · 10 Kash · 150 XP | `CK_Chest:Uncommon` · 25 Kash · 300 XP |
| 11–19 | 2,000 chips · 100 XP | 7,500 chips · 15 Kash · 200 XP |
| **20** ★ | 5,000 chips · 20 Kash · 250 XP | `CK_Chest:Rare` · 50 Kash · 500 XP |
| 21–27 | 2,500 chips · 125 XP | 10,000 chips · 20 Kash · 250 XP |
| **28** ★ | 10,000 chips · 30 Kash · 500 XP | `CK_Chest:Rare` · 150 Kash · 1,000 XP |
| 29–31 | 2,500 chips · 125 XP | 10,000 chips · 20 Kash · 250 XP |

The season-ending milestone sits at **node 28** so it is reachable in February; 29–31 are bonus
days that simply don't exist in shorter months.

Rough cycle totals: free ≈ 65k chips + 60 Kash; golden adds ≈ 200k chips + 500 Kash + 2 chests.
Golden price: **real money, ~$4.99/month subscription** (store product, §5.3). Sanity check the
Kash ratio against the cosmetics price list before launch.

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
[Index(nameof(UserId), nameof(PassKey), nameof(CycleKey), nameof(Node), IsUnique = true)]   // one node, once
[Index(nameof(UserId), nameof(PassKey), nameof(CycleKey))]                                  // the ladder read
public class PlayerPassClaim
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public Guid UserId { get; set; }
    [Required, MaxLength(32)] public string PassKey { get; set; }    // program: "monthly" (a Season Pass is another key)
    [Required, MaxLength(32)] public string CycleKey { get; set; }   // "2026-09"
    [Required] public int Node { get; set; }                         // = day of the cycle
    [Required] public DateTime ClaimedOnUtc { get; set; }            // the date the player actually tapped (audit; a
                                                                     // catch-up claim has ClaimedOnUtc > node's own day)
    [Required] public bool FreeGranted { get; set; }
    [Required] public bool GoldenGranted { get; set; }               // false if not entitled at claim time → retro-owed
    [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
}

[Table("PlayerPassEntitlements")]
[Index(nameof(UserId), nameof(PassKey), nameof(PurchaseRef), IsUnique = true)]   // one row per store transaction
[Index(nameof(UserId), nameof(PassKey))]
public class PlayerPassEntitlement
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public Guid UserId { get; set; }
    [Required, MaxLength(32)] public string PassKey { get; set; }
    [Required, MaxLength(32)] public string Source { get; set; }     // "iap" | "admin" | "gift"
    [Required, MaxLength(96)] public string PurchaseRef { get; set; } // store transactionId (admin grants: "admin:{guid}")
    [MaxLength(96)] public string OriginalTransactionId { get; set; } // the SUBSCRIPTION's id — renewals share it
    [Required] public DateTime StartsAt { get; set; }
    [Required] public DateTime ExpiresAt { get; set; }                // from the store's period end, NOT the cycle end
    public bool AutoRenew { get; set; }
    public DateTime? RevokedAt { get; set; }                          // refund / chargeback
    [Required] public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
}
```

**Why entitlement is a window, not a per-cycle row.** Golden is a monthly *subscription* bought on
any day, so its period (20th → 20th) crosses cycle boundaries. Each store transaction — the first
purchase and every renewal — appends its own row sharing an `OriginalTransactionId`; a player is
golden when **any** un-revoked row's `[StartsAt, ExpiresAt)` contains now. Append-only means the
billing history is auditable and a renewal can never silently shorten an existing entitlement.

The unique `(UserId, PassKey, CycleKey, Node)` index makes double-claiming a node **structurally
impossible**, whichever day it was claimed on; `(UserId, PassKey, PurchaseRef)` makes a replayed
receipt a no-op.

---

## 5. Service — `Services/Pass/PassService`

```csharp
public interface IPassService
{
    Task<PassStateDto> GetStateAsync(Guid userId, string passKey = "monthly");
    /// <summary>Claim ONE node. Omit the node for "today"; pass an earlier one to catch up
    /// (allowed only where the program's CatchUpPolicy permits it).</summary>
    Task<PassClaimResultDto> ClaimAsync(Guid userId, string passKey = "monthly", int? node = null);
    /// <summary>Claim every node the player is currently allowed to claim, oldest first.</summary>
    Task<PassClaimResultDto> ClaimAllAsync(Guid userId, string passKey = "monthly");
    /// <summary>THE entitlement seam. Records the subscription window + retro-enqueues owed golden
    /// rewards. Idempotent on purchaseRef. Callers: IAP receipt validation (source "iap") and the
    /// admin panel (source "admin"). There is NO in-game-currency purchase path.</summary>
    Task<PassPurchaseResultDto> GrantGoldenAsync(Guid userId, string passKey, string source, string purchaseRef,
        DateTime startsAt, DateTime expiresAt, string originalTransactionId = null, bool autoRenew = false);
    /// <summary>Refund / chargeback / admin revoke — closes the window. Collected rewards stay collected.</summary>
    Task RevokeGoldenAsync(Guid userId, string passKey, string purchaseRef, string reason);
}
```

### 5.1 What a player may claim right now

```
dayIndex   = (utcNow.Date - cycle.StartUtc.Date).Days + 1     // 1..daysInCycle
maxNode    = min(dayIndex, cycle.Length)                      // never ahead of the calendar
claimable  = nodes 1..maxNode not already claimed, filtered by CatchUpPolicy:
               None       → node == maxNode only
               GoldenOnly → node == maxNode, or (isGolden and node < maxNode)   ← default
               All        → every unclaimed node ≤ maxNode
```

Unclaimed nodes die with the cycle — there is no cross-cycle carry-over, and nothing is owed once
the month rolls over. That is the "T&C" behind catch-up, and the client must show the countdown.

### 5.2 Claim (money-safe order — reserve, grant, complete)

1. Resolve program + `cycle = PassCatalog.CurrentCycle(program, utcNow)`; off/absent →
   `Fail("No active pass.")`.
2. Pick the node: the requested one, else the highest claimable. Not claimable → `Fail` with the
   reason (already claimed / not yet / catch-up needs Golden / cycle complete). No row is written.
3. Insert `PlayerPassClaim { PassKey, CycleKey, Node, ClaimedOnUtc = utcNow.Date }` with both
   granted flags false. **A unique-index violation means a concurrent claim won** → reload that
   row and continue (idempotent replay) rather than failing.
4. Grant `node.Free` via `IRewardGrantService`, idemKey `pass:{passKey}:{cycleKey}:{user:N}:{node}:free`.
   Set `FreeGranted = true`.
5. If entitled (§5.3): grant `node.Golden`, idemKey `…:{node}:golden`, set `GoldenGranted = true`.
6. `CompletedAt = utcNow`, save.

If the process dies between 3 and 6 the row stays incomplete; the next claim call finds it and
re-runs the ungranted parts — each granter is idempotent, so nothing is lost and nothing pays
twice. Same pattern as `RewardService`.

### 5.3 Entitlement check

`IsGoldenAsync(userId, passKey, atUtc)` = **any** entitlement row for (user, passKey) with
`RevokedAt == null && StartsAt <= atUtc && ExpiresAt > atUtc`. Server-side only; the client's
`IsGolden` flag is display and never gates a grant.

Note the consequence of the subscription window: a player whose subscription lapses mid-cycle
keeps the golden rewards they already collected but stops earning new ones from that day — the
node's golden payload simply isn't granted, and `GoldenGranted` stays false. If they resubscribe
inside the same cycle, the retro pass (§5.4) picks those nodes back up.

### 5.4 Subscription (real money) + the missed-days unlock

**Golden is sold for real money only, as an auto-renewing monthly subscription.** The client buys
the store product, the server validates the receipt, and validation calls `GrantGoldenAsync`. No
chips/Kash/Gems path exists — an in-game-currency purchase is not a thing the API can express.

```
client → store subscription purchase → POST /api/pass/purchase { platform, productId, receipt }
      → IIapService.ValidateAsync(...)          ← Phase-0 IAP work, shared with the chip packs
      → PassService.GrantGoldenAsync(user, "monthly", "iap", transactionId,
                                     startsAt, expiresAt, originalTransactionId, autoRenew)
```

Rules for the IAP hop (they belong to the IAP spec, but the pass depends on them):
`transactionId` is the idempotency key end-to-end — a replayed receipt grants nothing new;
validation is **server-side against Apple/Google**, never a client claim; a mismatched product id
grants nothing. **Renewals** arrive as new transactions sharing the `OriginalTransactionId` and
simply append the next window — the store's renewal date is the source of truth, never our clock.
A **refund/chargeback/expiry** webhook calls `RevokeGoldenAsync`, which sets `RevokedAt`;
already-collected rewards are NOT clawed back (the ledger stays append-only), but any golden node
not yet *collected* from the inbox is expired at the same time — that is the "T&C apply".

**Dependency:** the paid track cannot go live before IAP receipt validation ships. Until then the
free track runs standalone and the admin grant (§6.2) covers QA/comps — the ladder, the retro
machinery and the entitlement rows are all exercised by that path, so nothing about Golden is
untested when IAP lands.

`GrantGoldenAsync`:
1. Insert the entitlement row (unique on `PurchaseRef` → a replay is a no-op).
2. **Unlock the missed days.** For every node `1..maxNode` of the CURRENT cycle:
   - already claimed with `GoldenGranted = false` → enqueue its golden lines into the inbox and
     set the flag;
   - never claimed at all → claim it now (a `PlayerPassClaim` with `FreeGranted` per policy and
     the golden payload enqueued), because the whole point of subscribing mid-month is getting the
     days you missed.
   Idempotency key `pass-retro:{passKey}:{cycleKey}:{user:N}:{node}:{i}`, so re-running the unlock
   (renewal, retry, resubscribe) can never pay a node twice.
3. Enqueued, **not** auto-credited — chips arrive only when the player taps collect (the existing
   inbox rule), which also gives the purchase a payoff moment worth animating.
4. Future claims grant golden inline (§5.2 step 5).

Scope of the unlock is the **current cycle only** — a subscription bought in October never pays
out September's ladder, no matter when the subscription started.

### 5.5 Reads

`GetStateAsync` returns the whole ladder + per-node claim state in one call (≤31 nodes is small):

```
Active, PassKey, CycleKey, Title, CycleStartUtc, CycleEndUtc, NextNodeUtc (tomorrow 00:00 UTC),
DayIndex, MaxNode, CatchUp (policy), 
IsGolden, GoldenUntilUtc, AutoRenew, GoldenProductId (per platform) + GoldenPriceUsd fallback,
ClaimableNodes[] (what the player may tap right now),
LockedByGoldenNodes (count — the buy-CTA number: "subscribe to unlock N missed days"),
Nodes[] { Index, IsMilestone, Free[], Golden[], Claimed, GoldenClaimed }
```

`RewardGrant` lines go to the client as-is (Kind + Id + Amount) so the client renders any future
kind generically — unknown kind → generic icon + label, never a crash.

---

## 6. API — `Controllers/PassController` (`[Authorize]`)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/pass` | full state (§5.5); `?passKey=` defaults to `monthly` |
| POST | `/api/pass/claim` | `{ node? }` — omit for today, pass an earlier node to catch up |
| POST | `/api/pass/claim-all` | claim everything currently claimable, oldest first |
| POST | `/api/pass/purchase` | `{ platform, productId, receipt }` → validate → `GrantGoldenAsync("iap")` |

## 6.1 Admin panel — `/Pass` (Khela.Web, full CRUD page)

Not a JSON textarea. This page is where the whole economy of the pass is decided, so it gets
the same treatment as the Chests editor but bigger. Cookie-Identity admin gate as elsewhere;
saves go through `PassCatalog.Validate` and write the `khela:pass` overlay atomically.

**Programs list** — every pass program (Key, Title, cadence, catch-up policy, node count,
Enabled, store product ids). `monthly` ships built in; **Create** is how a Season Pass gets added
later. **Delete** is blocked once any claim row references the key (disable it instead — history
must stay readable).

**Program editor** — title + cadence + catch-up policy + store product ids + display price, then
the **node grid** (the recurring ladder, plus a per-month **cycle override** tab for a themed
December). The header shows which cycle is live right now and how many days it has.

| Node | ★ | Free rewards | Golden rewards |
|---|---|---|---|
| 1 | ☐ | `Chips 1000` `XP 50` `+` | `Chips 3000` `Kash 5` `XP 100` `+` |

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
| POST | `/Players/{id}/grant-pass` | `GrantGoldenAsync(source: "admin")` with an explicit window — comp / support / QA |
| POST | `/Players/{id}/revoke-pass` | `RevokeGoldenAsync` — collected rewards are not clawed back |

The player detail page also shows the current cycle's claim history, golden state and renewal date.

---

## 7. Client (Unity, `PlayCard.Pass`) — after the server is green

- `PassScreen`: horizontal ladder, two rows (Free above, Golden below, locked rows dimmed with
  the subscribe CTA), milestone nodes visually larger, "Day N of 30 · next day in HH:MM ·
  cycle ends in D days".
- **The missed-days CTA is the screen's whole job**: earlier nodes the player can't claim show a
  golden lock and the count feeds "Subscribe to unlock N missed days". Don't bury it.
- Reward chips render from `RewardGrant` via a `RewardIconResolver` (kind + id → sprite + label),
  so a new kind is an art entry, not a code change.
- Claim → play the grant animation off the **server's returned applied amounts**, never local
  config values. Catch-up claims animate oldest-first via `claim-all`.
- Entry points: Home badge when anything is claimable, and the reward-inbox badge for the retro
  pile after subscribing.

---

## 8. Build order

| # | Slice | Ships |
|---|---|---|
| 1 ✅ | Reward seam | `RewardKind`/`RewardGrant`, 3 granters, `RewardGrantService`, `GrantXpAsync(bypassDailyCap)`, `AddRewardKindToPlayerRewards` |
| 2 ✅ | Catalog | `PassCatalog` + `Defaults()` + `Validate()` + unit tests (no DB, no HTTP) |
| 3 | Core | `AddPassTables`, `PassService` (state + claim), `PassController` GET/claim |
| 4 | Entitlement | `GrantGoldenAsync` + retro-enqueue + admin grant/revoke (exercisable without IAP) |
| 5 | Admin | `/Pass` full CRUD page (§6.1) + `ConfigBackupService` (§3.2) |
| 6 | Client | `PassScreen` |
| 7 | Real money | `POST /api/pass/purchase` wired to IAP receipt validation once that ships |
| — | later | `CosmeticGranter`, `ItemGranter` + `PlayerItems` |

Slices 1–2 are pure and testable with no infrastructure; do them first. Slice 7 is gated on the
Phase-0 IAP work — everything before it ships and is playable on the free track.

## 9. Tests (xUnit, alongside `ChestCatalogTests`)

- `Validate` rejects: `Tokens` line, undefined currency int, duplicate program key, duplicate or
  non-contiguous node index, ladder > 31 for a monthly program, non-positive amount, unknown chest
  key, unpayable kind, golden payload with no store product id, bad cycle-override key.
- `CurrencyGranter` drops a non-allowlisted line even when `Validate` was bypassed (fail-closed).
- Cycle math: Sept 15 → cycle "2026-09", day 15, maxNode 15; Feb 28 → maxNode 28 even with a
  31-node ladder; April 30 → 30; a milestone above 28 is flagged.
- Catch-up policy: free player on day 20 may claim only node 20; a golden player may claim 1..20;
  `None` allows only node 20 for everyone; `All` allows 1..20 for everyone.
- Retro/unlock: subscribing on day 20 unlocks exactly 20 nodes' golden payloads (claimed ones
  enqueued, unclaimed ones claimed), and a second `GrantGoldenAsync` (renewal/replay) enqueues 0.
- Replay: two concurrent `ClaimAsync` calls for the same node → one row, one payout.
- Cycle rollover: nothing from the previous cycle is claimable on the 1st.
- XP: a pass grant lands in full with the daily cap already exhausted; a *round* accrual with
  the same profile is still clamped (the bypass is opt-in and doesn't leak).
- Backup: unchanged config → no second file; changed config → new file; neither ever deletes.

## 10. Definition of done

1. `dotnet build` passes; migrations added deliberately; snapshot in sync.
2. No NON-NEGOTIABLE weakened — `Tokens` unreachable on every path, wagerability untouched.
3. Every grant idempotent on a stable key; the client is told the *applied* amounts.
4. Pass off (no program / no cycle / `Enabled=false` / bad config) pays nothing, breaks nothing.
5. The monthly cycle rolls over on the 1st with no admin action, and the subscription window is
   whatever the store says — never our own clock.
