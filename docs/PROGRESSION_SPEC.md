# Progression Spec — XP/Level, VIP/Status, Loyalty (for the build agent)

*Target: `khela/Khela.Game` (.NET 8, MySQL/Pomelo, Redis, SignalR). Conventions: DTOs in
`Khela.Common`; interface + DI registration; XML docs; deliberate EF migrations (never rename
columns); `RowVersion` concurrency; rate-limit new endpoints. Don't weaken any NON-NEGOTIABLE
rule in `CLAUDE.md`. `dotnet build` must pass with no unexpected pending migration.*

This spec is grounded in how shipping social casinos (Slotomania/Playtika Rewards, Huuuge Casino,
WSOP, Zynga Poker, DoubleDown, Bingo Blitz) actually build these systems. Sources at the bottom.
Numbers below are **tunable defaults** — put every one of them in config, not in code constants.

---

## Build status (audited 2026-06-28)

| # | System | Status | Notes |
|---|---|---|---|
| 1 | §6 Two-bucket wallet (clean/tainted) | ✅ **Done** | Earned-first spend, payout keeps the gifted fraction tainted, XP uses clean wager only; idempotent under `FOR UPDATE`. Stored as `Balance` + a single `GiftedBalance` slice (+ `WalletTransaction.GiftedDelta`), not literal `EarnedChips`/`GiftedChips`/portions — **mathematically equivalent**, accepted. |
| 2 | §2 XP & Level (core loop) | ✅ **Done** | Bet-proportional XP, the curve, win bonus, daily cap, level-up + every-10 milestone chip rewards, `GET /api/progression/me` + `ProgressionDto` — all live, wired into settle, runtime-tunable. |
| 3 | §4 Loyalty Points | ❌ **Not started** | Only a dead `LoyaltyPoints` field. No earn / store / redeem / DTO / endpoint. |
| 4 | §3 VIP / Status Points | ❌ **Not started** · design finalized 2026-06-29 | Code unbuilt (bare, never-written `VipTier`; 6 enum values vs 7 — migrate, don't cast). **Design redesigned** (see §3): Bronze auto-granted at Level 20 = floor/no-badge; Silver+ are scarce SP **+ spend-gated** badges; **flat ×1 SP** (multiplier on the Loyalty/benefit track only); **two separate windows** (tier 12mo / badge 30d); one gentle rolling-window decay. |
| 5 | §7 Anti-abuse | 🟡 **XP-side done** | Server-authoritative, min-bet floor, daily cap, idempotent. Claim/velocity/decay levers wait on the features that need them. |

**Deferred by design (ride in with the feature that needs them — NOT gaps in the core):**
- §2.1 secondary XP sources (daily-login/streak, quests/missions, first-purchase grants) → come with a login/quest system.
- §2.3 level-gated content unlocks → moot until there's more than blackjack to gate.

**Accepted deviations (working, intentional):**
- Min-bet is a level-tiered *soft* floor (sub-floor bets earn `0.2×`), not a single hard zero.
- Milestone reward (levels ending in 0) is a duplicate of the level-up chip credit, not a "bigger pack + boost".

**Cleanups owed:**
- `WinXpBonus` (+10% XP on a win) is **outcome-dependent** — minor tension with §1.2 / the legal posture. Listed here as a knob so it's intended, but get a lawyer nod or set it to `0`.
- `POST /api/progression/admin/daily-cap` writes the **retired** `progression:dailyXpCap` key nothing reads (live cap is the `khela:settings` hash) — delete or repoint.

**Next:** §4 Loyalty earn + store (the missing chip *sink*; earn folds into the existing settle accrual next to XP). **Blocked on IAP:** the spend side of Loyalty (`LpPerUsd`) and VIP (`SpPerUsd`) + VIP's monetization payoff — build System B alongside the IAP flow.

---

## 0. The three systems (and why they're separate)

The industry-standard split is **a play track and a status/spend track**, kept distinct so each
stays legally clean and behaviourally honest. Khela uses three counters:

1. **XP → Level** — *play progression.* Earned by **playing** (wagering). Drives content unlocks,
   level-up chip rewards, and a visible level badge. This is the "I'm getting better / further"
   loop. Money can *accelerate* it (buy chips → bet bigger → more XP) but can never *buy a level*.

2. **Status Points → VIP Tier** — *status.* Earned by **betting volume + IAP purchases + activity**
   — **never from the outcome of a wager** (this is the legal line; Playtika states it in bold).
   Status Points are **non-redeemable**: they only move you up tiers. VIP tier grants perks
   (bigger bonuses, store value, exclusive tables, flair, host).

3. **Loyalty Points → redeemable comp currency** — *the loyalty store.* Earned as a small fraction
   of wager (rake-style), multiplied by VIP tier. **Spent** in a loyalty store for chips,
   cosmetics, boosts, event entries. Non-cashable, and **never the token**.

> Why three, not one: XP must feel *earned by skill/time* (so it can't be bought outright); Status
> must reward *spend + volume* (so whales climb) without ever paying out value; Loyalty must be a
> *spendable reward* with its own sink. Merging them either makes leveling buyable (kills the
> earned feeling) or makes status redeemable (legal risk). Keep them separate.

---

## 1. HARD CONSTRAINTS (do not violate)

1. **Clean vs tainted wagers.** Chips obtained via **gift or peer trade** must grant **zero** XP,
   Status Points, and Loyalty Points. Only **earned** (won at a table, level/daily reward) or
   **purchased** (IAP) chips, when *wagered*, accrue progression. Accounting in §6.
2. **Accrue from wagering, not winning.** XP/Status/Loyalty accrue on the **bet placed** (the stake
   that flowed through `WalletService`), not on the payout. This is the anti-farming linchpin and
   the legal posture (status is never "based upon the outcome of any game").
3. **Non-cashable, no odds, no token.** Every reward in all three systems resolves to one of:
   bonus **Chips/Coins** (non-cashable), **cosmetics**, **convenience** (faster faucets, cooldown
   cuts, table access), or **status** (rank/badge). Never money, never real-world goods of value,
   never the Phase-2 token, and **never altered odds/RTP** (payers and free players face identical
   house edge — you buy playtime and status, never a better hand).
4. **Server-authoritative.** All accrual is computed server-side off the authoritative
   `WalletService` ledger. The client only *displays* level/tier/points from server DTOs.
5. **Minimum-bet floor.** Bets below a configurable floor grant no progression (kills zero/dust-bet
   grinding).

---

## 2. SYSTEM A — XP & Level (play-driven)

### 2.1 How XP accumulates
Primary source is **bet-proportional XP per round**, win or lose:

```
xpFromWager = floor(cleanWagerChips / XP_CHIPS_PER_POINT)   // default XP_CHIPS_PER_POINT = 10
```
- `cleanWagerChips` = the portion of the stake drawn from the **Earned/Purchased** bucket (§6).
- Only counts if the bet ≥ `MIN_BET_FOR_XP` (default 5,000 chips).
- Optional small **win bonus**: on a win, add `floor(xpFromWager * WIN_XP_BONUS)` (default 0.1).
  Keep small — XP is mostly for *playing*, not winning, so losers still progress.

Secondary sources (flat, config-driven, all subject to the daily cap):
- **Daily login / streak**: escalating per consecutive day (e.g., 200/300/400/…/cap).
- **Quests / daily missions**: fixed XP per completed task ("play 20 hands", "win 5 times").
- **First-purchase / milestone events**: one-off XP grants.

**Daily XP cap** (anti-bot / anti-no-life, Zynga-style): `DAILY_XP_CAP` default **150,000 XP/day**,
reset at server-local midnight. Excess is discarded (not banked). Caps the "buy chips, bet max,
hit max level in a day" exploit so leveling still costs *time*.

### 2.2 Curve (fast early, slow later)
Super-linear per-level delta. Default formula (tunable exponent/coefficient):

```
xpToNext(L) = round_to_50( XP_BASE * L^XP_EXP )   // XP_BASE = 150, XP_EXP = 1.6
```

Sample (defaults), illustrating the "treadmill that slows":

| Level → next | XP needed for that step | ~Cumulative XP |
|---|---|---|
| 1 → 2 | 150 | 150 |
| 2 → 3 | 450 | 600 |
| 3 → 4 | 870 | 1,470 |
| 5 → 6 | 1,950 | ~5,300 |
| 10 → 11 | 5,950 | ~33,000 |
| 20 → 21 | 18,100 | ~190,000 |
| 30 → 31 | 34,650 | ~520,000 |
| 50 → 51 | 78,450 | ~2.0M |
| 100 → 101 | 237,750 | ~13M |

- **No hard cap** (levels continue indefinitely for status), but **all content unlocks finish by
  ~level 60** — past that, leveling is pure prestige.
- Early levels are deliberately trivial (L2 in a few hands) to hook new players with rapid unlocks.

### 2.3 What Level is used for
- **Level-up chip reward**: `round_to_100(LVLUP_BASE * L)` (default `LVLUP_BASE = 10,000` chips) →
  +10,000 at L1, +200,000 at L20, scaling linearly while XP effort grows polynomially (classic shape).
- **Milestone reward** at every level ending in 0 (10, 20, …): a bigger chip pack + a free
  short XP/loyalty boost (the "round-number spike" that pulls players over slow stretches).
- **Content / feature unlocks gated by level** (you reach the level — you can't directly buy the
  unlock). Suggested map (tune freely):

| Level | Unlock |
|---|---|
| 1 | Blackjack low-stakes table; 1 starter slot |
| 3 | 2nd slot |
| 5 | Blackjack medium table; Teen Patti (3-card) unlocked |
| 8 | 3rd slot |
| 10 | Blackjack high table; daily-wheel upgrade |
| 15 | Tournaments / events entry |
| 20 | VIP-eligible high-limit tables visible |
| 30–60 | Remaining slots + cosmetic frames roll out |

- **Higher bet ceiling by level** (optional compounding loop, Slotomania-style): max bet-per-table
  scales with level so leveling literally raises how big you can bet.
- **Visible level badge** on profile, leaderboard, table seat.

---

## 3. SYSTEM B — VIP / Status Points (the prestige track)

> **Design intent (Reza, 2026-06-29): VIP is a SCARCE prestige/spend ladder, NOT a free-player ladder.**
> Free-player progression lives in XP/Level (§2). A VIP **badge** (Silver and up) must be *hard* — 30 days of
> continuous free play should not reach Silver. **Bronze is the floor, not a badge.** Status Points (SP) are
> non-redeemable (rank only); the per-tier multiplier is a **benefit on the Loyalty/store track, NOT on SP itself**
> (so a high tier never self-accelerates its own climb).

### 3.0 Bronze floor vs the VIP badge
- **Unranked** — below `VIP_ENTRY_LEVEL` (default **20**, tunable): not in the VIP system; no tier, no badge.
- **Bronze** — granted automatically on reaching `VIP_ENTRY_LEVEL`. It is the **floor** ("you're in the system"):
  **no VIP badge, baseline perks only.** SP is irrelevant at Bronze — Level alone grants it.
- **Silver → Black Diamond** — the actual **VIP badges**, earned by SP over the tier window **plus a spend floor**
  on the upper bands (§3.2). Deliberately scarce — a badge signals real spend / elite commitment, not time served.

### 3.1 How Status Points (SP) accumulate
SP only matter for climbing **above Bronze** (Silver+). Sources accrue at a **FLAT ×1 rate** — the tier multiplier
is NOT applied here (it lives on the benefit track, §3.5):

```
spFromWager    = floor(cleanWagerChips / SP_CHIPS_PER_POINT)   // divisor tuned (with the §3.2 bars) so free volume
                                                               //   ALONE can't reach Silver in a month; + a daily cap
spFromPurchase = floor(usdSpent * SP_PER_USD)                  // SP_PER_USD = 100; bigger packs get a bonus %
spFromActivity = fixed grants for login / quests / level milestones
```
- **Never from winnings** — the legal line (status not "based on the outcome of any game"). This includes *indirect*
  paths: keep `WinXpBonus = 0` (§2) so a win can't speed XP → level → activity-SP; activity SP rides only
  wager/time-driven milestones, never win-driven ones.
- `cleanWagerChips` = the Earned/Purchased portion only (§6) — gifted chips grant nothing.
- **`SP_FROM_WAGER_DAILY_CAP`** (tunable): caps SP earnable from wager volume per day, so a no-life grinder can't
  shortcut a badge by session length. IAP-driven SP is uncapped (real spend self-limits and is the intended fast
  path to a badge). The same `MIN_BET` floor as XP applies.

### 3.2 Tier table — Bronze floor + 6 earned badges
Tier = the highest band for which **both** the SP bar (over the tier window, §3.3) **and** the spend floor are met.
Numbers are **tunable defaults to recalibrate against real wager/spend telemetry before launch** — the *structure*
(flat ~×5 SP steps, spend-gated upper badges, scarce apex, multiplier on benefits not SP) is the fixed part.

| Tier | Badge | Target % of payers | SP bar (trailing tier window) | + Spend floor (trailing, BD-rescaled) | Benefit ×mult (Loyalty/store) |
|---|---|---|---|---|---|
| Bronze | — floor, **permanent** | ~all engaged | n/a — granted at `VIP_ENTRY_LEVEL` | — | ×1.0 |
| Silver | ✅ | ~60% | 50,000 | — | ×1.3 |
| Gold | ✅ | ~22% | 250,000 | — | ×1.7 |
| Platinum | ✅ | ~10% | 1,250,000 | ~$30 (Tk ~3.5k) | ×2.2 |
| Diamond | ✅ | ~4% | 6,250,000 | ~$150 (Tk ~18k) | ×2.8 |
| Royal Diamond | ✅ | ~1% | 31,000,000 | ~$800 (Tk ~95k) | ×3.6 |
| Black Diamond | ✅ | ~0.2% | 150,000,000 | ~$4,000 (Tk ~480k) | ×4.5 |

*All numbers are starting defaults — instrument live telemetry and tune (per-tier population is unpublished
industry-wide; operators set these post-launch). Calibrate the SP divisor + bars BACKWARD from the target
distribution above, which reproduces the empirical "top ~5% of payers ≈ ~48% of revenue."*

**Locked design decisions (validated by the 2026-06-29 industry research):**
- **Bronze is PERMANENT** (level-gated, never decays) — no one ever falls out of VIP entirely. Playtika trained
  social-casino whales to expect *permanent* status, so visible demotion is the top churn risk; the floor absorbs it
  (Marriott "soft-landing" pattern handles the rest, §3.4).
- **Spend floors are BD-rescaled ~10× down** from Western anchors (Bangladesh mobile ARPU ~$16/yr ≈ ¼ of global):
  apex ≈ $4k, not $40–80k. **Present floors as "SP earned from purchases," NEVER a literal "$X = Diamond"** (a visible
  dollar gate reads pay-to-win). Silver/Gold are SP-only; spend gates start at Platinum.
- **Spend is IAP-only at launch** (no external/MFS payment yet). The SP-from-purchase hook is **RAIL-AGNOSTIC** —
  it credits SP + spend on a *verified purchase event* (USD amount + idempotency key), not off an Apple/Google
  receipt — so the later **coinfolytics web store** (adds bKash/Nagad, skips the store cut) is just a second feed and
  VIP needs no change. Spend floors stay dormant until IAP ships.
- **Benefit multiplier widened to ×1.0→×4.5** (market tops ~×5–6); applies ONLY to the Loyalty/store track.
  **Hard-cap it so a top tier never costs more to serve than it pays** (model IAP-margin at the ceiling — the 2026
  "VIP-era" failures were margin inversion).
- **7 tiers + names are exact industry standard** (Playtika); flat-SP + multiplier-on-redeemable-only is *ahead* of
  Playtika (which multiplies SP itself = rich-get-richer). The existing `VipTier` enum is **6 values vs 7** — add the
  missing band via a deliberate migration (**map, never cast**).
- **"Hide my VIP badge" opt-out** (KamaGames ships this) — cheap differentiator; part of the badge-display contract.

### 3.3 The two windows — tier rank vs badge display (separate, configurable knobs)
Do **not** conflate them:
- **`TIER_WINDOW_MONTHS`** (default **12**): the SP lookback that sets your **earned rank**. Slow to climb, slow to
  lose. Tier = the band your SP over this window qualifies for (with the spend floor).
- **`BADGE_WINDOW_DAYS`** (default **30**, a *separate* knob): the badge stays **lit** only while you meet its
  maintenance bar within this *shorter* window. Go quiet past `BADGE_WINDOW_DAYS` and the **badge greys out — COSMETIC
  ONLY; it NEVER lowers your rank/tier**; re-meeting the bar relights it instantly. So a badge is **hard to earn**
  (long window, high bar + spend) and **must stay active to wear** (short window). Rank only ever moves on the
  12-month window at the monthly review (§3.4). (Research note: the closest keep-active precedent, MGM, uses 90 days;
  keeping the 30d effect purely cosmetic avoids over-punishing episodic spenders.)

### 3.4 Decay — ONE model, gentle
- **Promotion is immediate**: cross a band's SP bar (and spend floor) → promoted at once (`max(current, band)`).
- **Tier follows the rolling tier window**: at each monthly review, tier = `band(SP over TIER_WINDOW_MONTHS)`. The
  window's natural roll-off **IS** the decay — there are **no separate step-down targets** (the old
  Black→Platinum / Royal→Gold rules are **deleted**; they contradicted the rolling sum and produced cliff demotions).
- **Softeners** (anti-churn): demote **at most one tier per review**; require **two consecutive** reviews below the
  bar before any demotion (grace); **hysteresis** — the demote bar sits ~15–20% below the promote bar so boundary
  players don't yo-yo; never demote a player whose trailing **spend** still holds the band.
- **Inactivity / win-back**: prolonged inactivity decays rank via the window roll-off and dims the badge via
  `BADGE_WINDOW_DAYS`. On return, **lead with a win-back** (restore the badge on first login back, offer a short
  boosted-SP "reclaim your status" window) — never confront the player with a demotion screen.

### 3.5 VIP perks (Silver+ only; scale by tier; all non-cash)
Bronze gets baseline only — perks begin at the first **badge** (Silver). The **benefit multiplier** (×1.0 → ×3.5,
§3.2) boosts the **Loyalty / store track**, NOT SP. Per tier (tune freely):
- **Loyalty-Point + store-value boost** (the ×multiplier) — more comp value per dollar as you climb.
- **Bigger daily free-chip gift + bigger daily wheel.**
- **More chips per IAP dollar** at higher tiers (the monetization payoff — a tier-gated discount).
- **Exclusive high-limit tables / invite-only tournaments** at Platinum+.
- **More daily gift redemptions** and bigger social-gift multipliers.
- **Cosmetic prestige**: the tier badge/frame, name flair, table aura — the visible flex.
- **Dedicated VIP host / concierge** at Diamond+.
- **Faster faucets / reduced cooldowns.**
- Differentiate the **top** bands with exclusivity/cosmetics/host, **not** ever-deeper per-dollar discounts (that
  inverts unit economics — you'd discount your biggest spenders most).
> None of these alter odds. They grant more chips, more access, more status, more convenience.

### 3.6 VIP Level (1–10) — the premium multiplier ladder (Reza, 2026-06-29)
The TIER ladder (§3.0–3.5) is the *earned* prestige; on top sits a separate **VIP Level 1–10** — the real prestige +
the big multiplier. **ALL multipliers apply to the COMP/FAUCET tracks (Loyalty earning, daily bonus, wheel, store
value) — NEVER to winnings.** Odds stay identical for payers and free players; the §1.3 non-negotiable holds. (This
supersedes the §3.2 "Benefit ×mult" column — that ×1.0→×4.5 is replaced by the small TierBonus below.)

**Effective comp boost = 1 + TierBonus + VipLevelBonus** (additive; all tunable):
- **TierBonus** — small, the "experienced player" signal: Bronze +1% → Black Diamond +15%.
- **VipLevelBonus** — the real lever: VIP 1 +20% → VIP 10 **+170%**. (So a Black-Diamond VIP 10 ≈ **+185%** comp.)

**Earn a VIP Level (both deliberately hard):**
- **Buy** from the store — rail-agnostic purchase hook ("VIP Booster" IAP item; live once IAP / web-store ships).
- **Grind** — a `VipLevelProgress` accumulator ticks each settled round by `roundSP × tierFactor` (Bronze ×1 → Black
  ×3, so a higher tier grinds VIP faster); per-level thresholds are huge, so even VIP 1 is a long road.

**Maintained, or it decays 1 level/month** (floor 0 — you CAN fall out of VIP entirely; unlike Bronze there's no free
floor here):
- The monthly review drops VIP Level by **1** if it wasn't maintained that period.
- Maintained by ANY of: **playing** (a settled round within `VipMaintainDays` = 30), spending **Loyalty Points**
  (live now), or an **IAP "VIP Booster" top-up** (a small keep-up charge, cheaper than buying a level fresh — dormant
  until IAP). Future rails ("TC"/etc.) plug into the same maintain seam.

**Buildable now:** the level field + the grind + the combined comp multiplier + the monthly decay + LP-maintenance.
**Dormant until IAP:** buy-a-level + the VIP-Booster top-up (the rail-agnostic seam is ready).

---

## 4. SYSTEM C — Loyalty Points (redeemable comp currency)

### 4.1 Earn (rake-style, the real-casino comp logic)
```
lpFromWager = floor(cleanWagerChips / LP_CHIPS_PER_POINT) * tierMult   // LP_CHIPS_PER_POINT = 100  (~1% comp)
lpFromPurchase = floor(usdSpent * LP_PER_USD) * tierMult               // small bonus drip on IAP
```
- Earn rate deliberately **low** so the store stays aspirational, not a fast faucet.
- Same clean-wager rule and minimum-bet floor.

### 4.2 Burn (the Loyalty Store)
A server-defined catalog (config/JSON), each entry `{ id, kind, costLP, payload, minVipTier? }`.
Sample prices (tune):

| Item | Cost (LP) |
|---|---|
| 1,000 chips | 10 |
| 10,000 chips | 90 |
| 1-hour XP boost (×2) | 50 |
| Tournament entry token | 30 |
| Cosmetic avatar frame (limited) | 250 |
| Profile flair / table theme | 150 |

- Redemptions credit **Chips into the Earned bucket** (they're a reward of play, kept clean) or
  grant a cosmetic/entitlement. **Never** chips into a cashable path; **never** the token.
- All redemptions go through `WalletService` (idempotent on a `CorrelationId`) so the loyalty
  store can't double-spend.

> Existing `LoyaltyPoints` field = this redeemable balance. **Add a separate `StatusPoints`
> (lifetime + windowed) for VIP** — don't overload `LoyaltyPoints` for tier, or spending loyalty
> would drop your VIP tier. They must be independent counters.

---

## 5. Distinguishing play vs spend (summary table)

| Counter | Earned by | Multiplied by tier? | Redeemable? | Drives |
|---|---|---|---|---|
| **XP** | wagering (clean) + login + quests | no | no | Level → unlocks, level-up chips |
| **Status Points** | wagering volume (clean) + IAP + activity; **never winnings** | yes | **no** (tier only) | VIP tier → perks |
| **Loyalty Points** | small fraction of clean wager + IAP drip | yes | **yes** (store) | Loyalty store purchases |

---

## 6. Clean-vs-tainted wager accounting (the hard part)

**Two-bucket wallet.** Split the chip balance into two tracked sub-balances on `PlayerWallet`:
- `EarnedChips` — from table winnings, IAP purchases, level/daily/loyalty rewards. **Clean.**
- `GiftedChips` — from gifts and peer trades. **Tainted** (never accrues progression).

(Total displayed balance = `EarnedChips + GiftedChips`. Keep both as `decimal(18,4)`.)

**Bet (debit) rule** — in `WalletService.DebitForBet`:
1. Draw the stake from `EarnedChips` first, then `GiftedChips` for any remainder.
2. Record the split on the `WalletTransaction` (add `EarnedPortion` + `GiftedPortion`, signed
   deltas that sum to `Amount`).
3. **Progression accrual uses `EarnedPortion` only** as `cleanWagerChips`. The gifted portion earns
   nothing.

**Settlement (credit) rule** — to stop gift→clean laundering: a payout is credited back to the
buckets **in the same proportion the stake was drawn from**. If a 100-chip bet was 100% gifted, the
200-chip win credits 100% to `GiftedChips`. If it was 70 earned / 30 gifted, split the payout
70/30. This keeps tainted money tainted through wins.

**Why this exact shape:** it's deterministic, idempotent (rides the existing `CorrelationId`
ledger), auditable (`BalanceBefore/After` per bucket), and it closes the laundering hole without a
fragile "chip lineage" graph. *This touches the money path — implement it in small, reviewable
steps with tests, behind the existing idempotency + `SELECT … FOR UPDATE` locking. Do not weaken
wallet integrity.*

**Migration note:** when adding the buckets, migrate existing balances into `EarnedChips`
(treat all current chips as clean) and default `GiftedChips = 0`.

---

## 7. Anti-abuse (build these in from day one)

- **Wager-based accrual through `WalletService`** — the single biggest lever. Bots that farm free
  chips and min-bet earn ~nothing; the server (not the client) decides what counts.
- **Minimum-bet floor** (`MIN_BET_FOR_XP` / loyalty / SP) — dust bets grant zero.
- **Daily XP cap** + **one-claim-per-device-per-day** on daily bonus/wheel/login (reuse the
  device-id you already derive for guest auth as a fingerprint primitive).
- **Velocity limits** on action/claim endpoints (reuse the Redis Lua rate-limiter).
- **Inactivity-based perk suspension** + apex-tier decay so farmed bursts don't lock in status.
- **Collusion / chip-dumping**: **low priority for blackjack** (player-vs-dealer, no player-to-
  player transfer at the table). Becomes relevant for Teen Patti / poker — flag a backlog item to
  detect repeated one-way losses between the same accounts and shared-device multi-seating. Not
  needed for the launch blackjack slice.

---

## 8. Data model & backend mapping

**Existing (reuse):** `UserProfile.VipTier`, `UserProfile.LoyaltyPoints` (→ redeemable balance),
`UserProfile.Level`, `UserProfile.Experience`, `UserGameStats` (GamesPlayed/Won/etc.),
`WalletService` (authoritative ledger), `PlayerWallet`.

**Add (deliberate migrations):**
- `PlayerWallet.EarnedChips` (`decimal(18,4)`), `PlayerWallet.GiftedChips` (`decimal(18,4)`).
- `WalletTransaction.EarnedPortion` + `WalletTransaction.GiftedPortion` (`decimal(18,4)`, signed).
- `UserProfile.StatusPoints` (lifetime, `bigint`) + a **monthly SP ledger** (`StatusPointsLedger` keyed by
  (UserId, month)) so the **tier window** (`TIER_WINDOW_MONTHS`) is a sum over the trailing buckets. Store SP at a
  FLAT ×1 rate — the multiplier is NOT applied to SP (§3.1).
- Trailing **spend** accumulator (for the upper-band spend floors) — a USD column on the same monthly ledger, or a
  `UserProfile.TrailingSpend` rolling sum.
- **Badge state** (`UserProfile.BadgeLitUntil` / last-SP-activity timestamp) so the **badge window**
  (`BADGE_WINDOW_DAYS`, separate from the tier window) can grey out / relight the badge independently of rank.
- `UserProfile.LastWageredActivityAt` (inactivity rule; debounce writes). (No `StickyVipTier` / step-down map needed
  — the rolling tier window IS the decay, §3.4.)
- `VIP_ENTRY_LEVEL` is a config knob, not a field: Bronze is derived from `UserProfile.Level >= VIP_ENTRY_LEVEL`.
- `UserProfile.DailyXp` + `DailyXpResetAt` (or Redis counter) for the daily cap.

**Services:**
- `IProgressionService` (new): `AccrueForWager(walletTxn)` called by `WalletService` after a clean
  debit — computes XP/SP/LP from `EarnedPortion`, applies tier multiplier, daily cap, min-bet
  floor; handles level-up (chips via `WalletService` credit + unlock checks). Idempotent on the
  wager's `CorrelationId`.
- `IVipService` (new): tier = `band(SP over TIER_WINDOW_MONTHS)` **and** the band's spend floor; Bronze auto-granted
  at `VIP_ENTRY_LEVEL` (no badge). Tracks the badge separately on `BADGE_WINDOW_DAYS` (lit only with recent activity).
  Exposes perks (the benefit multiplier, daily-bonus size, store-value boost) as a `VipPerks` value object other
  services read. Monthly review applies the gentle one-tier-max decay (§3.4) — no fixed step-down map.
- `ILoyaltyStoreService` (new): catalog + redeem (idempotent via `WalletService`).
- DTOs in `Khela.Common`: `ProgressionDto` (level, xp, xpToNext, dailyXpRemaining),
  `VipStatusDto` (tier, statusPoints, nextTierAt, perks), `LoyaltyDto` (points, catalog).
- Endpoints (`[Authorize]`, rate-limited): `GET /api/progression/me`, `GET /api/vip/me`,
  `GET /api/loyalty/store`, `POST /api/loyalty/redeem`.

**Config (all defaults above live here, not in code):** a `Progression` config section —
`XpChipsPerPoint, XpBase, XpExp, DailyXpCap, MinBetForXp, WinXpBonus, LvlUpBase, SpChipsPerPoint, SpPerUsd,
SpFromWagerDailyCap, LpChipsPerPoint, LpPerUsd, VipEntryLevel, TierWindowMonths, BadgeWindowDays,
VipTierSpThresholds[], VipTierSpendFloors[], VipBenefitMultipliers[], InactivityDays`. Note: `VipBenefitMultipliers`
apply to the **Loyalty/store track, NOT to SP** (§3.1/§3.5); they replace the old `VipMultipliers`, and
`VipTierSpThresholds` + `VipTierSpendFloors` replace the old single `VipTierThresholds`.

> **Runtime-tunable (since the admin dashboard, 2026-06-26):** every `Progression:*` key is now an
> admin-editable *override* in the Redis hash `khela:settings`, which `ProgressionService` overlays onto
> the appsettings base on each accrual (`ProgressionMath.Overlay`, lenient parse + fallback — a bad value
> can't break accrual; the `Enabled` master switch is intentionally NOT overridable). Edit them live in
> **Khela.Web ▸ Settings ▸ Casino** — applies on the next round, no restart. The old standalone
> `progression:dailyXpCap` key is retired (folded into the hash). See `docs/ADMIN_DASHBOARD.md`.

---

## 9. Build order (phasing)

1. **Two-bucket wallet + portion tracking** (§6) — foundation; everything else reads `EarnedPortion`.
2. **XP/Level accrual + curve + level-up rewards + unlock gating** (§2). Most visible to players.
3. **Loyalty Points earn + store** (§4) — gives a chip *sink* and a reason to keep playing.
4. **VIP/Status Points + tier + perks + decay** (§3) — the monetization/status layer; pairs with
   the IAP flow (you need real purchases to feed SP/LP from spend).
5. **Anti-abuse hardening** (§7) — min-bet floors and daily caps from the start; collusion tooling
   deferred to the poker phase.

---

## Definition of done
- `dotnet build` passes; migrations added deliberately; snapshot in sync.
- Two-bucket wallet split + per-txn portion recorded; gifted chips provably earn **zero**
  progression; payout composition keeps tainted chips tainted; all idempotent under `FOR UPDATE`.
- XP accrues from clean wager only, respects min-bet + daily cap; curve matches config; level-up
  credits chips and unlocks gate by level.
- Status Points accrue from clean wager + IAP + activity (**never winnings**); tier computed with
  multiplier; apex decay + inactivity suspension work.
- Loyalty Points accrue and redeem through `WalletService` (idempotent); store rejects insufficient
  balance and unknown ids.
- No NON-NEGOTIABLE weakened: non-cashable throughout, token never dispensed, odds identical for
  payers and free players, server-authoritative accrual.

---

## Sources (industry patterns)
- Playtika Rewards official rules (Status Points, 7 tiers, "not based on game outcome", annual
  window): https://www.playtikarewards.com/rules/
- Huuuge Casino Help Center — VIP tiers + permanent/decay rules:
  https://huuuge.helpshift.com/hc/en/4-huuuge-casino/faq/2487-cards-in-huuuge-rewards---your-status-symbols/
- DoubleDown Diamond Club (purchase-driven loyalty) + Loyalty Points:
  https://support.doubledowncasino.com/hc/en-us/articles/210931083-How-Loyalty-Points-work
- Slotomania level curve + bet-proportional XP (wiki): https://slotomania.fandom.com/wiki/Level
- Zynga Poker XP rules + level cap + daily XP cap:
  https://zyngasupport.helpshift.com/hc/en/27-zynga-poker/faq/995-how-do-i-increase-my-experience-points-xp-and-level/
- WSOP club tiers (6×5) + point redemption: https://www.pokernews.com/free-online-games/play-wsop/clubs.htm
- Gamezebo — faucets/sinks as monetization plumbing:
  https://www.gamezebo.com/news/plumbing-for-revenue-how-sinks-faucets-levers-drive-in-game-monetization/
- Lloyd Melnick — VIP/high-roller program design (status > points; treat VIPs as relationships):
  https://lloydmelnick.com/tag/vip/
- Fingerprint / IDnow — bot, multi-account, chip-dumping detection:
  https://fingerprint.com/blog/betting-bots/ , https://www.idnow.io/glossary/chip-dumping/
