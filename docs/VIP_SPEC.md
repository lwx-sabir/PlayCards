# VIP redesign — three points, three jobs (Reza, 2026-08-23)

*Status: BUILDING — steps 1-3 of §7 are in (currencies + channels; LP on the wallet; seasons + SP on the wallet). Steps 4-5 remain. Reviewed by three independent critics (economy/abuse, hard rules, feasibility) on 2026-08-23;
six findings folded in — see §9. Supersedes PROGRESSION_SPEC §3 (VIP/Status), §4 (Loyalty) **and the equal-house-edge clause
of §1.3** (the win bonus + rebate is a post-settle promotion — §4); §2 (XP/Level) is untouched. Every number below is a
dev-time default — all of them land on admin editors.*

## 0. The one-paragraph version
Three point currencies, each with ONE job. **SP** is status: how active you are this season — it drives the **badge**
(Bronze → Black Diamond), resets each season, and gives a **comp boost**. **LP** is loyalty: how often you play — spendable
(LP → chips in the Exchange) plus an **LP Score** that only ever rises. **VIP-P** is what you have **bought** — store only —
and it drives **VIP level 1–10**, the **real multiplier**: a % on comps AND a % of your day's net winnings, or a rebate on
your day's net losses, credited on the way home with a popup. VIP decays on a lifetime you can extend or top up from the VIP
store. VIP is always **additive** on top of everything else — it never replaces, never skips.

## 1. The three currencies

| | **SP** — status | **LP** — loyalty | **VIP-P** — VIP points |
|---|---|---|---|
| measures | activity + wager this season | how often you play | how much you've bought |
| earned by | play · packs · rewards · world | play · packs · rewards · world | **store purchases only** |
| drives | badge tier (Bronze…Black Diamond) | spendable balance + **LP Score** (lifetime) | VIP level 1–10 |
| spend / reset | resets at **season end** to a lower tier | **exchange** LP → chips | decays on a **lifetime**; extend / top up in the VIP store |
| benefit | **comp boost** (LP + XP earn) for the season | — | comp boost **+ win bonus % / loss rebate %** |

**All three become wallet currencies** (`CurrencyType.Sp = 5`, `Lp = 6`, `VipPoints = 7` — append-only; non-wagerable; never
Tokens). That is why the rest is cheap: packs, rewards, chests, missions, the Exchange and the 3D world grant or exchange them
through `RewardGrant` lines and the wallet ledger exactly like Chips (idempotent, `FOR UPDATE`, `BalanceBefore/After`). A pack
is `Lines: Chips 2,000,000 · Kash 50 · VipPoints 500`.

**But one allowlist is not enough.** Today `RewardCurrencies.Allowed` is the single gate for *every* channel — reward granters,
the store validator, the exchange validator. Appending all three there would let a chest mint VIP-P and an admin author
Chips → VIP-P. So there are **three explicit allowlists** (one check per channel; fail-closed; Tokens on none):

| channel | allowed | gate |
|---|---|---|
| **Grantable** — pass / chests / missions / daily / level-ups / world | Chips · Coins · Gems · Kash · **Sp · Lp** — never VipPoints | `CurrencyGranter`, `RewardService`, the catalogs' validators |
| **Sellable** — store product lines | Grantable **+ VipPoints** | `StoreCatalog.Validate`, `StorePurchaseService` |
| **Exchangeable** — exchange pairs | FROM ∈ {Chips · Coins · Gems · Kash · **Lp**}; TO ∈ {Chips · Coins · Gems · Kash} — Sp and VipPoints on **neither** side, Lp never a TO | `ExchangeCatalog.Validate`, `ExchangeService` |

## 2. SP and the badge (seasonal status)
- **Earn:** play — `Sp:ChipsPerPoint` (50) of clean wager per SP, daily cap `Sp:DailyCap` (0 = uncapped, genuinely); packs
  (explicit `Sp` lines — the *automatic* $→SP rate `SpPerUsd` is gone, that job moved to VIP-P); rewards / world via lines. SP
  from play is never from winnings.
- **Tier = band of the current season's SP.** The spend floor is **gone** (money is VIP-P's job). Tier table (admin, 8 rows
  None…Black Diamond): `SpBar`, `CompBoost %`, `ResetTo`. Bronze stays the free floor at `VipEntryLevel` (player level 20).
- **BUILT (step 3):** the tier is the band of the SP WALLET BALANCE, since nothing debits SP until the roll — so the band the
  balance reaches IS the tier climbed to (no peak column), mid-season demotion is impossible, and `ReviewTierAsync` keeps only
  PROMOTION (the old monthly one-tier-max decay + `DemoteHysteresis` answered a trailing sum that could shrink; a season
  answers it with a scheduled reset instead). The monthly `StatusPointsLedger` buckets are no longer written — the wallet
  ledger is the audit — and trailing SPEND, needed only if an admin re-imposes a floor, now comes from `StorePurchases`.
  Season 1 opens on first run and SEEDS every player's SP to the bar of the tier they already hold, which is the migration off
  the trailing-window model: every badge is preserved without pretending a 12-month sum was ever a season total.
- **Seasons** — new `Seasons` table (`StartUtc`, `EndUtc`, `Status`); admin knob `Season:LengthDays`, **0 = lifetime / never
  resets**; every SP credit carries the season id. At season end a job (replaces the monthly tier review) does, per player:
  `newTier = ResetTo[climbedTier]`, season SP := `SpBar[newTier]` (a ledger debit — or credit, if the bar is above the balance —
  so it audits). `LifetimeStatusPoints` keeps rising (display).
- **Benefit:** the tier's `CompBoost %` joins the comp multiplier (§4) — boosts LP **and XP** accrual (XP boost is new).

## 3. LP (loyalty)
- **Earn:** play — `Lp:ChipsPerPoint` (100) of clean wager per LP × comp multiplier; packs / rewards / world via lines.
- **Spend:** **one Exchange pair** `lp_chips` (LP → Chips) in the Exchange admin — rate, step, min, caps, all the knobs that
  exist. The Exchange admin shows the pair's implied **comp %** (`chipsPerLp ÷ Lp:ChipsPerPoint`) so the number that makes LP a
  comp (≈1%) or a loop (≈100%) is never set blind.
- **LP Score** = lifetime LP credited (never drops) — profile / leaderboards. Derived from the LP wallet's ledger (sum of
  positive movements), cached on `LifetimeLoyaltyPoints`; play accrual updates the cache inline, a nightly job reconciles it
  (so pack / reward / world LP counts too).
- **Migration is a MOVE with a single cut-over, not a copy**: per player, one transaction — `CreditAsync(Lp, profile.LoyaltyPoints,
  AdminAdjustment, "lpmig:{userId}")` + `UPDATE UserProfiles SET LoyaltyPoints = 0 WHERE … AND LoyaltyPoints = @snapshot` (retry on
  0 rows), idempotent on the correlation id. In the **same deploy** every writer and reader moves to the wallet (accrual, VIP
  maintain, admin grants) and the old LP store (`Loyalty:Catalog`) is retired the moment the pair goes live — never an overlap in
  which the same LP is spendable twice.

## 4. VIP (the money track)
- **VIP-P:** credited only by store products (`Lines: VipPoints N`), including dedicated **VIP store** products that sell VIP-P
  and **time**. The Starter Pack carries exactly enough VIP-P for VIP 1.
- **Level** = `max(windowBand, heldLevel)` where
  - `windowBand` = band of **VIP-P credited in the trailing `Vip:WindowDays`** (e.g. 90; computed from the VIP-P wallet ledger by
    `CreatedAt`, cached on the profile and refreshed on every VIP-P credit) — stop buying and the window empties: **that is the decay**;
  - `heldLevel` is a **snapshot**: `HeldLevel` + `HeldThrough` on the profile. A VIP-P credit that reaches or keeps a band sets
    `HeldLevel = windowBand`, `HeldThrough = now + HoldDays[HeldLevel]`; a credit *below* the band it would need does **not**
    refresh (a $1.99 pack with 10 VIP-P can't hold VIP 10 forever). `vip_booster_time` extends `HeldThrough` only (stacking capped
    at 2 × HoldDays ahead; level unchanged). `vip_booster_level` = +1 over `windowBand` for `HoldDays` (temporary). When
    `HeldThrough` passes and the window is lower, the level is the window band — no monthly −1 step any more.
  - Level table (admin, rows 1…N): `VipPointsRequired`, `CompBoost %`, `WinBonus %`, `LossRebate %`, `HoldDays`, `DailyCapChips`.
- **Comp multiplier** = `1 + SpTierBoost + VipLevelBoost` (additive, as today) on LP + XP earn — never on odds.
- **Win bonus / loss rebate — the new part.** The intent: a VIP **always sees chips coming back** after play. The rule that
  delivers it *without* being a chip printer (§9-A explains why the obvious version prints):
  - **One net basis, one window the server closes.** Accrual is per **player-local day** (the same local-day rule the pass uses),
    one open row per (player, day): `CleanNet += round net × cleanRatio`, with `cleanRatio = clean stake ÷ total stake` (the ratio
    the payout already uses — gifted chips earn nothing, §5). Done in the settle roll-up beside XP/SP/LP/piggy, keyed per
    `(roundId, seat)`, never inside settle; the table's RTP and the engine are untouched.
  - **The level at accrual is stamped on the row** (`LevelAtAccrual` = max level seen that day).
  - **Pay on the CLOSED day**: `winBonus = WinBonus%[lvl] × max(0, CleanNet)` **or** `rebate = LossRebate%[lvl] × max(0, −CleanNet)`
    — a day is either up or down, so exactly one line pays; a player who was up *and* lost some hands gets the win line
    (the losses are already inside the net). Both capped by the level's `DailyCapChips` (**0 = feature OFF, not uncapped** — the
    exchange's convention) and by `Vip:RebateMaxPctOfHandle` (a % of clean handle per day, default 0.25%).
  - **Claim** = `POST /api/vip/rebate/claim` on the way home releases every closed, unpaid day (guarded `UPDATE … WHERE Paid = 0`,
    wallet credit `TransactionType.VipRebate = 8` keyed `viprb:{rowId}` — idempotent, re-drivable). Today's open row is shown as
    "accruing" in the popup, not paid. Rows expire unpaid after `Vip:RebateExpiryDays` (30).
  - The popup shows every line, the day it belongs to, and says so when a cap clipped.
- **Why this is a promotion, not a game change (the licensing line):** nothing touches a shoe, a paytable or a settle; the accrual
  lives in the game-extension layer (`Progression:Enabled`) and ships **off** in the engine build; the credit is a separate,
  audited `VipRebate` type; the engine's house edge is identical for payers and free players — the player's *return* differs by a
  post-settle cashback, which real-money operators run routinely. **Precondition for build step 5: a lawyer nod** on "% of
  net daily winnings as a VIP cashback" (PROGRESSION_SPEC already flags the smaller `WinXpBonus` the same way).

## 5. Guardrails (restated for the new currencies)
- SP / LP / VIP-P are **non-wagerable** (`IsWagerableCurrency` stays Chips/Coins only) and **never Tokens**.
- **VIP-P is never rewarded by play and never exchanged** (Grantable / Exchangeable lists above): it is the record of money.
- **SP is never exchanged** (it is status, not value); LP → chips is a comp by design and the Exchange's round-trip rule still
  holds (Chips → LP can't exist — Lp is never a TO).
- The rebate accrues on **clean** stake only and the credit is clean (`CreditGiftedAmount = 0`) — gifted (minted) chips can't be
  laundered into earned chips through it; PROGRESSION_SPEC §1 hard constraint 1 holds.
- Win bonus / rebate: one net basis, server-closed days, level-at-accrual, caps that fail closed, a %-of-handle ceiling, expiry.
  The admin VIP-level editor shows per row the **expected return per 1M wagered** against the house edge, so a +EV level can't
  be set blind.
- Every credit goes through `IWalletService` with a correlation id; every rate lives on an admin table.

## 6. What changes, concretely
| area | change |
|---|---|
| `CurrencyType` | + `Sp`, `Lp`, `VipPoints` (5/6/7); the **three allowlists** (§1) replace `RewardCurrencies.Allowed` as the gate per channel; `IsWagerable` unchanged; the economy dashboard's currency switches get the three |
| wallet DTO / HUD | `WalletBalances` + the three; client HUD binders for LP / VIP-P (SP shows as the badge) |
| SP | accrual credits wallet `Sp` (season); `StatusPointsLedger` retired → `Seasons` + ledger; monthly tier review → **season roll job**; tier table loses spend floor + grind factor, gains `ResetTo` |
| LP | accrual credits wallet `Lp`; MOVE migration + single cut-over; `Loyalty:Catalog` and the profile column retired; LP Score cached from the ledger |
| VIP | `VipLevelProgress` retired; `HeldLevel`/`HeldThrough` + cached window band on the profile; level table gains `VipPointsRequired`, `WinBonus %`, `LossRebate %`, `HoldDays`, `DailyCapChips`; `VipRebateAccruals` (player, local day) + claim endpoint + `TransactionType.VipRebate` |
| store | packs carry `Sp` / `Lp` / `VipPoints` lines; VIP store section (VIP-P packs, time, level); Starter Pack = VIP 1 |
| admin | Status tiers (bar, comp %, reset-to) · VIP levels (6 columns + expected-return column) · Seasons (length, current, roll now) · rebate knobs · the Exchange pair |
| client | `ExchangeState` carries LP → chips once the pair exists; **VIP popup on home** (Reza's UI; I give `VipRebateState` + the claim call) |
| code invariants | the XML docs on `VipPerks` / `VipMath` / `LoyaltyService` that assert "never winnings" are rewritten to "never odds; the cashback is a post-settle promotion" |

## 7. Build order (each step green + reviewed before the next)
1. Currencies + the three allowlists + wallet + DTOs + HUD binders (no behaviour change yet).
2. LP → wallet (MOVE migration, single cut-over) + the Exchange pair; retire the LP store.
3. Seasons + SP → wallet + tier table + season roll.
4. VIP-P + level from window / held snapshot + VIP store products + Starter Pack.
5. Rebate accrual (clean, per local day, level-stamped) + claim + `VipRebate` + popup state. **Needs the lawyer nod.**

## 8. Open (small)
- Season length default (30 days?) and the first season's start — at deploy, or a date you pick.
- Defaults for the level table's `WinBonus % / LossRebate % / DailyCapChips` — the editor's expected-return column makes these
  safe to set, but they are yours.
- Whether the LP → chips rate should be **tier-dependent** (higher status = better rate) — one more column.

## 9. What the design review changed (2026-08-23, three critics; findings judged against the code)
- **A — the rebate as first written was a chip printer.** "Rebate on every losing hand" + "win % on net since the player's last
  claim" lets a client claim after every winning round, turning `max(0, net)` into `Σ max(0, round net)`: bonus on gross wins plus
  rebate on gross losses ≈ (win% + rebate%) × half the handle per round — above blackjack's ~0.5% edge at ~1%, unbounded with
  the cap at 0. Fix: §4 — one net basis per server-closed local day, pay win **or** rebate, level stamped at accrual, caps
  fail closed + a %-of-handle ceiling, expiry. The intent survives: every day with play pays something back.
- **B — one allowlist would have let chests mint VIP-P and the Exchange sell it.** Fix: three allowlists (§1).
- **C — gifted chips would launder into clean through the rebate.** Fix: clean-ratio accrual, clean credit (§4/§5).
- **D — it contradicted PROGRESSION §1.3 while claiming to supersede only §3/§4.** Fix: §0 supersedes §1.3 explicitly; the
  licensing framing + lawyer precondition in §4; invariant docs listed in §6.
- **E — the LP migration as a "copy" made LP spendable twice during the overlap, and the LP Score stopped being fed.** Fix: MOVE +
  single cut-over + ledger-derived Score (§3).
- **F — "held level" was undefined, so a $1.99 pack could hold VIP 10 forever; the accrual row had a claim/settle race.** Fix:
  held snapshot rules, per-(round, seat) guards, guarded close, corr id from the row (§4).
