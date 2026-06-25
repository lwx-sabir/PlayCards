# Progression Spec — XP/Level, VIP/Status, Loyalty (for the build agent)

*Target: `khela/Khela.Game` (.NET 8, MySQL/Pomelo, Redis, SignalR). Conventions: DTOs in
`Khela.Common`; interface + DI registration; XML docs; deliberate EF migrations (never rename
columns); `RowVersion` concurrency; rate-limit new endpoints. Don't weaken any NON-NEGOTIABLE
rule in `CLAUDE.md`. `dotnet build` must pass with no unexpected pending migration.*

This spec is grounded in how shipping social casinos (Slotomania/Playtika Rewards, Huuuge Casino,
WSOP, Zynga Poker, DoubleDown, Bingo Blitz) actually build these systems. Sources at the bottom.
Numbers below are **tunable defaults** — put every one of them in config, not in code constants.

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

## 3. SYSTEM B — VIP / Status Points (status track)

### 3.1 How Status Points (SP) accumulate
Three sources, each multiplied by the player's **tier multiplier** (§3.3):

```
spFromWager    = floor(cleanWagerChips / SP_CHIPS_PER_POINT) * tierMult   // SP_CHIPS_PER_POINT = 50
spFromPurchase = floor(usdSpent * SP_PER_USD) * tierMult                  // SP_PER_USD = 100, bigger packs get a bonus %
spFromActivity = fixed grants for login/quests/level-up * tierMult
```
- **Never** from winnings. Bet volume, real-money purchases, and activity only.
- `cleanWagerChips` again = Earned/Purchased portion only (§6).

### 3.2 Tier table (7 tiers — the modal industry choice)
Tier = highest band your **trailing-12-month SP** qualifies for. Thresholds (tunable):

| Tier | Trailing-12-mo SP | SP/earn multiplier |
|---|---|---|
| Bronze | 0 – 999 | ×1.0 |
| Silver | 1,000 – 9,999 | ×1.5 |
| Gold | 10,000 – 49,999 | ×2.0 |
| Platinum | 50,000 – 249,999 | ×3.0 |
| Diamond | 250,000 – 1,499,999 | ×4.0 |
| Royal Diamond | 1,500,000 – 7,999,999 | ×5.0 |
| Black Diamond | 8,000,000+ | ×6.0 |

The **compounding multiplier** (×1 → ×6) is the workhorse retention lever: high tiers earn SP
faster, so the climb accelerates. (Existing `VipTier` field holds the tier; see §7.)

### 3.3 Maintenance / decay
Tier is reviewed on a rolling schedule (monthly). If the player's **trailing-12-month Status
Points** fall below the threshold needed to hold their current tier, they **step down by the rule
below** — a soft landing, not an immediate crash to whatever band their points currently qualify
for. Apex tiers decay hard (volatile by design); lower tiers decay gently, one step at a time:

- **Black Diamond** → drops to **Platinum**.
- **Royal Diamond** or **Diamond** → drops to **Gold**.
- **Any other tier** → drops **one tier** (Gold→Silver, Silver→Bronze, …), with **Bronze** as the floor.

A player who keeps missing the maintenance bar at successive reviews continues stepping down until
they reach the tier their activity actually supports (Bronze at the floor). **Promotion is
immediate**: crossing a higher band's SP threshold at any time promotes them straight back up (take
`max(currentTier, bandFromTrailing12moSP)`); decay only ever applies at a scheduled review.

Plus a universal **inactivity rule**: after `INACTIVITY_DAYS` (default 180) with no real wagered
activity, **suspend perks** (not the tier) until the player returns.

Implementation: persist `VipTier` + the trailing-12-mo SP (monthly ledger, §8); the monthly job
compares SP against the current tier's threshold and applies the step-down rule above. Keep the
step-down map in config so it's tunable.

### 3.4 VIP perks (scale by tier; all non-cash)
- **SP/Loyalty earn multiplier** (×1 → ×6) — the core compounding perk.
- **Bigger daily free-chip gift + bigger daily wheel** per tier.
- **Store value boost / vouchers**: more chips per IAP dollar at higher tiers (the monetization
  payoff — effectively a deepening, tier-gated discount).
- **Exclusive high-limit tables / invite-only tournaments** at Platinum+.
- **More daily gift redemptions** and bigger social-gift multipliers.
- **Cosmetic status**: collectible tier badge/frame, name flair, table aura.
- **Dedicated VIP host / "Ambassador" support** at Diamond+ (human touch for top spenders).
- **Faster faucets / reduced cooldowns**.
> None of these alter odds. They grant more chips, more access, more status, more convenience.

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
- `UserProfile.StatusPoints` (lifetime, `bigint`) + a windowed accumulator for trailing-12-mo
  (either a `StatusPointsLedger` table keyed by month, or `StatusPointsWindowStart` + a rolling
  sum — a monthly ledger is cleaner for decay math).
- `UserProfile.StickyVipTier` (the highest non-apex tier ever earned).
- `UserProfile.LastWageredActivityAt` (for inactivity rule; debounce writes).
- `UserProfile.DailyXp` + `DailyXpResetAt` (or Redis counter) for the daily cap.

**Services:**
- `IProgressionService` (new): `AccrueForWager(walletTxn)` called by `WalletService` after a clean
  debit — computes XP/SP/LP from `EarnedPortion`, applies tier multiplier, daily cap, min-bet
  floor; handles level-up (chips via `WalletService` credit + unlock checks). Idempotent on the
  wager's `CorrelationId`.
- `IVipService` (new): recompute tier from trailing-12-mo SP + sticky tier; expose perks
  (multiplier, daily-bonus size, store-value boost) as a `VipPerks` value object other services
  read. Nightly/periodic job for decay + inactivity suspension.
- `ILoyaltyStoreService` (new): catalog + redeem (idempotent via `WalletService`).
- DTOs in `Khela.Common`: `ProgressionDto` (level, xp, xpToNext, dailyXpRemaining),
  `VipStatusDto` (tier, statusPoints, nextTierAt, perks), `LoyaltyDto` (points, catalog).
- Endpoints (`[Authorize]`, rate-limited): `GET /api/progression/me`, `GET /api/vip/me`,
  `GET /api/loyalty/store`, `POST /api/loyalty/redeem`.

**Config (all defaults above live here, not in code):** a `Progression` config section —
`XpChipsPerPoint, XpBase, XpExp, DailyXpCap, MinBetForXp, WinXpBonus, LvlUpBase, SpChipsPerPoint,
SpPerUsd, LpChipsPerPoint, LpPerUsd, VipTierThresholds[], VipMultipliers[], InactivityDays`.

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
