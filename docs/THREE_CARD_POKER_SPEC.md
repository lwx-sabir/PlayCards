# Three Card Poker — Research & Build Spec (for the agent)

*Target: `khela/Khela.Game` (.NET 8) backend + `Khela.Play` Unity client. Honors all CLAUDE.md
NON-NEGOTIABLES: server-authoritative, non-cashable Chips/Coins, dual-currency firewall, idempotent
wallet, provably-fair shuffle. 3CP is a multiplayer vs-DEALER table game that reuses the blackjack
engine almost 1:1.*

---

## 0. Framing — and the one finding that matters

Three Card Poker (Derek Webb, 1994; now Light & Wonder) is **a multiplayer table game, not single-
player** — exactly like your blackjack: N seats, **one shared dealer hand**, each seat settles
against that dealer hand **independently**. It is the *same shape* as blackjack but **simpler**: one
binary decision per seat (**Play or Fold**) instead of a hit/stand/double/split loop, and the dealer
hand is fixed at three cards (no draw logic — "qualify" is just a comparison threshold). Settlement
is a pure paytable lookup once the two 3-card hands are known.

So it reuses your existing infrastructure wholesale and adds almost nothing new:
- **Reused as-is:** multi-seat table model, lobby/auto-match, the round-driver tick, SignalR push,
  provably-fair commit-reveal shuffle, idempotent `(round, seat)`-keyed wallet settle, per-hand
  settle audit, two-bucket clean/tainted wallet.
- **Genuinely new (small, self-contained):** a **3-card hand evaluator** (straight > flush) and a
  handful of **paytable lookup tables**. That's it.
- **Absent entirely (vs a PvP pot/ring game):** no pot/escrow, no blind/seen, no side-show, **no
  collusion surface** (seats never interact — each is a sealed contest vs the bank), and **no table-
  liquidity requirement** (one player + dealer is a complete game — no "waiting for players").

### THE finding: one engine, multiple house-banked 3-card skins
The 3CP engine is **branding-agnostic** — one server implementation skins into several **house-banked
3-card *vs-dealer* table games** purely via the config-driven `GameDefinition` (key / displayName /
branding) with **paytables as data**: different name, different paytable, identical server logic. A
house-banked 3-card vs-dealer product that is literally 3CP rules (dealer qualifies on Queen-high, a
"pair or better" side bet, a 6-card bonus) is an established, shippable category, so additional skins
cost almost nothing.

> **Scope guardrail — this spec is Three Card Poker and house-banked 3-card *vs-dealer* variants ONLY.**
> It is **not** the authentic Indian/Bengali **Teen Patti**, and makes no claim to be: that is a **PvP
> pot game** (blind/seen/side-show, players vs each other, no dealer) with entirely different rules,
> social mechanics, and player expectations — a separate, deliberate build (`TEEN_PATTI_SPEC.md`), out
> of scope here. Do **not** market a 3CP skin as authentic Teen Patti. (Note too that some live products
> *named* "Teen Patti" — "20-20 / Bet on / 1-Day" — are a different format again: a two-hand A/B
> "bet-behind" with no dealer and no qualification; they likewise do not share this engine.)

---

## 1. Rules (implementation-grade)

Single 52-card deck, **reshuffled every hand** (no carryover, no counting). Server deals, shuffles
(provably-fair), settles. Ace high or low for straights (A-2-3 lowest, Q-K-A highest).

### 1.1 Ante / Play flow
1. **Betting window:** each seat posts an **Ante** (mandatory to be dealt) + optional side bets
   (Pair Plus, 6-Card/3+3, Prime, Progressive) — all **before** the deal.
2. **Deal:** 3 cards to each seat (visible to that player), 3 to the dealer **face-down**. **All cards
   — including the dealer's 3 — come from the single deck committed at shuffle time (provably-fair
   commit–reveal); the dealer's cards are predetermined and merely *hidden*, never drawn after seats act.**
3. **Per-seat decision (the only turn):** after seeing its own 3 cards, each seat independently
   chooses **PLAY** (post a Play bet **exactly equal to the Ante**) or **FOLD** (forfeit the Ante).
4. **One dealer reveal** after all seats have acted.
5. **Settle-all** against the single dealer hand.

### 1.2 Dealer qualification + settlement (all branches)
**Dealer qualifies on Queen-high or better** (qualifies ≈ 69.6% of hands).

| Branch | Ante | Play |
|---|---|---|
| Dealer does **NOT** qualify | pays **1:1** | **pushes** (returned) |
| Dealer qualifies, **player wins** | pays **1:1** | pays **1:1** |
| Dealer qualifies, **player loses** | loses | loses |
| **Tie** (card-by-card equal) | push | push |

- **Tie = card-by-card** after category: sort each hand descending, compare rank[0],[1],[2]. **Ace ranks high (value 14, above King=13) in every hand EXCEPT the A-2-3 wheel — there it plays low (value 1) and the straight's high card is the 3; never give the Ace one fixed value, and never 13 (it would tie the King), or one of the two straights mis-ranks.** Suit
  never breaks ties in the base game. *Config flag:* "ties go to player" variant (Ante+Play pay 1:1)
  lowers the house edge ~0.13pp and slightly shifts optimal strategy — default to **push**.

### 1.3 Ante Bonus (pays regardless of dealer)
Paid on the Ante for a **straight or better**, **independent of the dealer's hand and of
win/lose/tie** — *but only if the player PLAYED* (a fold forfeits it; no straight-or-better hand
would rationally fold, but enforce: bonus only on Played hands).
- **It pays even on a LOSING hand** (e.g. player straight loses to dealer trips → still collects the
  straight bonus). Implement as a **separate additive payout** keyed only on `(Played == true) AND
  (hand ≥ straight)` — never gated on winning the showdown or on dealer qualifying.
- **Standard schedule (Table 1, ship this):** Straight **1:1**, Three-of-a-kind **4:1**, Straight
  flush **5:1**.

### 1.4 Side bets (all one-sided, independent of Fold/Play and of the showdown)
All placed pre-deal; all resolve regardless of the Ante/Play outcome. **Critical asymmetry vs the
Ante Bonus: side bets STILL PAY EVEN IF THE PLAYER FOLDS the Ante** (the player's 3 cards are still
revealed). This is the single most common implementation bug — code it explicitly.

- **Pair Plus** — wins on **a pair or better** in the player's own 3 cards (some house-banked variants
  label the same bet **"Pair or Better"**). Configurable paytable (see §3) — model it as an **arbitrary
  ordered tier list**, including an optional **top-line tier** (mini-royal / three-aces), not a fixed 5 rows.
- **6-Card Bonus / "3+3"** — best **5-card** poker hand from **player's 3 + dealer's 3**; pays on
  **three-of-a-kind or better**. **Uses standard 5-card ranking** (flush > straight here — a second,
  separate evaluator). Resolved after dealer reveal.
- **Prime** — color bet: all 3 player cards same color → **3:1**; all 6 player+dealer same color →
  **4:1** (≈3.62% edge). Optional.
- **Mini Royal** — not a standalone bet; a **top line** (suited A-K-Q) inside other paytables.
- **Progressive** — optional fixed-prize/jackpot side bet on Mini-Royal/strong hands. Defer.

### 1.5 Optimal strategy (advice only — does not change the protocol)
**Play if your hand is Queen-6-4 or better, else fold.** Card-by-card: Q-7-2 plays, Q-6-5 plays,
Q-6-3 folds, any J-high folds, any pair+ always plays. (Q-6-4 is the exact EV break-even.) This is
just strategy guidance for the player/UI hint; the server protocol is simply {Play, Fold}.
"Mimic-the-dealer / play any Queen" ≈ 3.45% (near-optimal); "always play" = 7.65% (wrong).

### 1.6 Hand ranking (3-card — straight beats flush)
| Rank | Hand | Combos / 22,100 | Prob |
|---|---|---|---|
| 1 | Straight flush | 48 | 0.217% |
| 2 | Three of a kind | 52 | 0.235% |
| 3 | **Straight** | **720** | **3.258%** |
| 4 | **Flush** | **1,096** | **4.959%** |
| 5 | Pair | 3,744 | 16.94% |
| 6 | High card | 16,440 | 74.39% |

**Straight ranks ABOVE flush** because with 3 cards a straight (720) is rarer than a flush (1096) —
inverse of 5-card poker. **Write a dedicated 3-card evaluator; do NOT reuse a 5-card straight/flush
order.** (Build this 3-card evaluator once and share it across any 3-card skin.) Note the 6-Card Bonus
needs a **separate 5-card evaluator** (flush > straight).

### 1.7 Bet limits, currency & exposure
- **Currency: Chips only** — 3CP tables are **Chips-denominated**, the same wagerable currency as
  blackjack (the engine stays currency-agnostic internally). Never accept the token, Coins, or Gems at
  a 3CP table; wagering is restricted to Chips at the wallet boundary (CLAUDE.md §2 dual-currency guardrail).
- **Per-circle limits — independent:** every bet circle (**Ante, Pair Plus, 6-Card/3+3, Prime**) carries
  its **own config-driven `min`/`max`**, unrelated to the Ante; **Play** is always exactly the Ante, so it
  inherits the Ante's bound. Validate **server-side** at the betting window — reject out-of-range,
  missing-Ante, or unknown-circle bets **before** any debit.
- **Per-circle max-win cap — config, generous/off by default:** an optional payout ceiling that bounds
  liability on the high-multiplier side bets (6-Card royal 1000:1, mini-royal top line 50–200:1). Keep it
  off/generous for the non-cashable social build; **enable it when modelling real-money liability at
  scale** (standing "treat everything as real money" directive).
- **Correlated exposure (design note):** the **dealer's 3 cards are shared by all seats**, so a
  dealer-favourable hand can trigger **many seats' 6-Card Bonuses at once** — per-seat payouts stay
  independent and fair, but **aggregate round liability is correlated**, not a sum of independent draws.
  Factor this into variance/bankroll modelling and into sizing the 6-Card max-win cap.

---

## 2. Architecture — reuse the blackjack vs-dealer table

### 2.1 Round-driver states (a strict simplification of blackjack)
`BettingOpen → Dealing → PlayerActing (seat-by-seat, single Play/Fold) → DealerReveal (once) →
Settle (all seats) → Complete`.
- Reuse the `BlackjackRoundDriver` tick. The per-seat turn is **one binary action**, not a loop, so
  turn logic is *simpler* than blackjack. **Timeout default = auto-FOLD** (don't put more money at
  risk without consent).
- Dealer has **no draw logic** — the 3 cards are fixed; "qualify" is a comparison.

### 2.2 Settlement = independent paytable line items (idempotent, reuses blackjack settle)
Once a seat's 3 cards and the dealer's 3 cards are known, **every payout is a deterministic lookup**
with zero branching on other seats. For each seat compute a fixed set of signed deltas — Ante±,
Play± (push on no-qualify), Ante Bonus+, Pair Plus+, 6-Card+, Prime+ — and apply as **one wallet
settle batch keyed on `(roundId, seatId)`** (or per-line `CorrelationId` like `3cp:{round}:{seat}:ante`).
- **Debit-on-bet** at the betting window (Ante mandatory; Play debited on the Play action; side bets
  debited up front). Signed-delta invariant holds per line.
- **Credit-on-settle** is a single idempotent pass under `SELECT … FOR UPDATE`, dedup on
  `CorrelationId` — re-running settle for the same `(round, seat)` is a no-op. **Exactly the
  blackjack credit-gross-on-settle shape.**
- **Two-bucket wallet honored:** Ante/Play/side-bet debits draw earned-first and record their gifted
  slice; payouts keep the stake's gifted ratio (no laundering); XP basis = clean net. Reuse §6 of
  PROGRESSION_SPEC verbatim.
- Fully **replayable for audit** from the committed deck + each seat's Play/Fold decision — fits the
  existing `GameHandParticipant` per-hand settle audit (a seat with side bets is just extra ledger
  line items under the same round+seat key).

### 2.3 What you do NOT build (vs a PvP pot/ring game)
No pot escrow, no blind/seen, no side-show, no collusion detection, no table-liquidity machinery —
that's why 3CP is cheap.

---

## 3. Paytables + house edges (all data/config, never hardcoded)

The 3-card distribution is fixed, so any paytable's exact chip return is computable instantly
(`EV = Σ payout×combos − loser_combos, ÷ 22,100`). In a **non-cashable social build these schedules
are your "win-rate dial,"** not a legal constant — tune them to a target chip burn-down / session
curve, and you can even run **player-positive** schedules for events (impossible in a real casino,
free with re-grantable chips).

**Ante Bonus** (multiplier on Ante): **ship Table 1 = SF 5 / Trips 4 / Straight 1 → 3.37% edge**
(element of risk 2.01%). Reduced variants: 1/3/4 → 3.83%, 1/2/3 → 4.28%. Generous online: down to
1.56% / 0.33%.

**Pair Plus** (SF / Trips / Straight / Flush / Pair):
| Schedule | Edge | Note |
|---|---|---|
| 40 / 30 / 6 / **4** / 1 | **2.32%** | legacy "full pay" — generous, retention-friendly **(recommended default)** |
| 40 / 30 / 6 / **3** / 1 | **7.28%** | modern casino norm (flush dropped 4→3) |
| 40 / 25 / 5 / 4 / 1 | 6.75% | |
The **single most sensitive lever is the flush payout 4↔3** (swings ~5pp). Some house-banked 3-card
paytables add a **top-line tier** — a mini-royal (suited A-K-Q) or three-aces row paying ~50:1 up to
100:1+ — so model the paytable as an **arbitrary ordered tier list**, not a fixed 5 rows.

**6-Card Bonus / 3+3** (best 5-card from 6, **standard 5-card ranking — flush > straight**): there are
**seven documented pay tables ranging 8.56% → 18.98% house edge** (Wizard of Odds). The most generous is
**Version 1-A = 8.56%**; the **10.22%** schedule is **WoO "Version 1-D"** (Royal 1000 / SF 200 / Quads 50
/ FH 25 / Flush 20 / Straight 10 / Trips 5) — a **land-based Scientific Games** table, *not* a verified
Ezugi-specific one (don't credit it to Ezugi). ⚠️ Every table except 1-A is **10–19% edge**, so never
copy a schedule blindly; **ship 1-A (8.56%)** if you offer it at all.

**Prime** 3:1 / 4:1 → **3.62%**. **Combined Ante+Play optimal house edge ≈ 3.37% on the Ante
(element of risk ≈ 2.01%).** Reference RTPs to validate against: Ante ≈96.63%, Pair Plus ≈95.51%,
6-Card ≈91.44%.

---

## 4. Edge cases to handle in code
1. **Side bets pay on a FOLD; Ante Bonus does not.** (The asymmetry above — the #1 bug.)
2. **Ante Bonus pays on a LOSING hand** — evaluate it independently of the showdown.
3. **Dealer no-qualify → Play PUSHES, Ante pays 1:1, Ante Bonus still pays** — a seat can win
   Ante + Bonus while Play merely pushes.
4. **Two evaluators:** base game + Pair Plus = 3-card (straight > flush); 6-Card Bonus = 5-card
   (flush > straight, with full house / quads / royal).
5. **Tie = exact card-by-card** after category; suit never breaks it (suits matter only for
   flush/straight-flush categorization, Prime, and Mini-Royal-in-spades lines).
6. **Single deck per hand, no duplicates across seats** — total cards dealt = 3×seats + 3, all from
   one freshly shuffled 52-card deck; ensure no card collision across seats in a deal.
7. **Ace is dual-valued:** low (=1) **only** in the A-2-3 wheel (its high card is the 3 → the lowest
   straight); high (=14, above the King's 13) everywhere else — A-K-Q is the top straight, and the Ace
   is high in every non-straight comparison. Never encode the Ace as a single fixed value, or as 13
   (it would collide with the King).
8. **Dealer cards are committed, not drawn:** the dealer's 3 cards are fixed by the committed deck at
   shuffle (before any Play/Fold) and revealed only after all seats act — never selected in response to
   player decisions. Settlement is fully replayable from the committed deck + each seat's Play/Fold.

---

## 5. Recommendation
- Ship **base Ante/Play + Ante Bonus Table 1 (5/4/1)**, **push on tie**, **Pair Plus default
  40/30/6/4/1 (2.32%, generous)**; gate **6-Card/3+3, Prime, Progressive, Mini-Royal** behind config.
- The engine is **branding-agnostic**: ship "Three Card Poker" first, and (optionally, later) any
  additional **house-banked 3-card *vs-dealer* skin** from the same `GameDefinition` — branding +
  paytables as data, reusing blackjack with no pot/collusion/liquidity cost.
- **Out of scope here:** the authentic PvP Teen Patti (blind/seen/side-show, players vs each other) is a
  different game with different expectations — a separate, deliberate build (`TEEN_PATTI_SPEC.md`), not a
  skin of this engine. Do not market a 3CP skin as authentic Teen Patti.

---

## 6. Definition of done
- Reuses the multiplayer vs-dealer table/round-driver/SignalR/settle infra; no parallel netcode; no
  pot/collusion/liquidity machinery.
- Server-authoritative; client only renders + sends the single Play/Fold action.
- Dealer qualifies on Q-high; all four settlement branches correct (incl. Play-push on no-qualify).
- Ante Bonus pays only on Played hands, on straight+, independent of dealer (incl. on a loss). Side
  bets pay independently **including on a fold**.
- 3-card evaluator correct (**straight > flush**, A-2-3 low); separate 5-card evaluator for 6-Card.
- Per-seat settle is an idempotent `(round, seat)` batch of signed wallet deltas; two-bucket wallet
  honored; replayable from the committed deck. Paytables are config/data, not hardcoded.
- Optional additional house-banked 3-card *vs-dealer* skins via `GameDefinition` (branding + paytables
  as data); NOT the authentic PvP Teen Patti. `dotnet build` green; deliberate migrations; no
  NON-NEGOTIABLE weakened.

---

## Sources
- Wizard of Odds — Three Card Poker (math, paytables, house edges, Q-6-4, combinatorics; updated
  2025-12-03): https://wizardofodds.com/games/three-card-poker/
- Wikipedia — Three Card Poker (structure, heads-up-vs-dealer, hand frequencies, ~3.37%):
  https://en.wikipedia.org/wiki/Three_Card_Poker
- Evolution Live Three Card Poker full rules + paytables + RTPs:
  https://www.livedealer.org/rules/evolution_3card_poker_rules.htm
- Evolution/Ezugi "Teen Patti 3 Card" (3CP branded as Teen Patti; Pair-or-Better, 3+3 Bonus):
  https://games.evolution.com/live-casino/live-poker/teen-patti-3-card-2/
- Nevada GCB / California BGC / NH Lottery rules-of-play PDFs (regulator paytables as filed).
- Galaxy Gaming "Three Card Prime" (Prime side bet): https://www.galaxygaming.com/products/three-card-prime
