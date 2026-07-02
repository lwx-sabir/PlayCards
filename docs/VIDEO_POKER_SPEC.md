# Video Poker — Research & Build Spec (for the agent)

*Target: `khela/Khela.Game` (.NET 8) backend + `Khela.Play` Unity client. Honors all CLAUDE.md
NON-NEGOTIABLES: server-authoritative, non-cashable Chips/Coins, dual-currency firewall, idempotent
wallet, provably-fair shuffle.*

---

## 0. Framing — "slots + one decision" (and the multiplayer caveat)

Core video poker is **single-player vs a fixed paytable**: deal 5 cards → player HOLDs some → DRAW
replaces the rest → evaluate the 5-card hand → pay per a paytable. **No dealer, no opponents, no
pot, no table.** Architecturally it is **a slot machine with one player decision (the hold)** — it
belongs to the *slots family*, not the *table family*.

So unlike blackjack/Teen Patti/3CP it has **no multiplayer table, no pot, no dealer logic, no
collusion surface, and no table-liquidity requirement** (a single player is a complete, sellable
session). That makes it the **cheapest and safest game to bolt onto the money path** — but it also
means the "multiplayer/social" element is **bolted on around the outside** (tournaments,
leaderboards, community jackpots, missions), never intrinsic to the hand. If the goal is genuinely
*social* play, video poker delivers it through a **score-based competition layer**, not through
shared cards. Decide that's acceptable before committing — this is a solitaire game with a social
wrapper, not a multiplayer table.

What's genuinely new to build is small: a **hold/draw turn**, a **5-card evaluator** (incl. wilds),
**per-paytable config**, and optionally **multi-hand fan-out** + the **tournament/jackpot** wrappers.
Everything else is reused from blackjack/slots and the Phase-1 social stack.

---

## 1. Core game loop

```
DEAL  → server shuffles a deck (provably-fair commit-reveal), deals first 5, publishes deck hash, debits bet
HOLD  → client sends a hold mask (which of the 5 indices to keep). Client renders only — never generates cards.
DRAW  → server reveals replacement cards from the COMMITTED deck (positions 6..N), forms the final 5
EVAL  → server runs the 5-card evaluator → hand rank
PAY   → one paytable lookup: payout = paytable[rank] × coins; credit on settle (idempotent)
```

- **Deck:** single 52-card deck per hand (53 with one joker for Joker Poker; 54 for Deuces+Joker).
  The 5 dealt cards are removed; draws come from the remaining 47/48. **One draw only**, hold 0–5.
- **Single highest category pays** (categories don't stack).

### Provably-fair fit (identical to the blackjack shuffle)
- Before the deal, the server derives the shuffled deck (server seed + client seed + nonce, HMAC-
  SHA256) and **publishes the deck-hash commitment** with the dealt 5 cards.
- **The draw cards are pre-committed positions in the already-hashed deck.** The hold arrives
  *after* the hash is published, and the draw is revealed from the **committed** deck — so the
  player can verify the draws were **not chosen to dodge their hold** (the one fairness fear unique
  to video poker). Reveal the seed at round end; player re-hashes to confirm. Reuse Khela's existing
  commit-reveal mechanism verbatim.

---

## 2. Hand evaluation (NORMAL 5-card poker order)

Royal Flush > Straight Flush > Four of a Kind > Full House > **Flush > Straight** > Three of a Kind
> Two Pair > One Pair > High Card. **Flush beats straight here** — the *opposite* of Teen Patti /
3CP (5 cards invert the 3-card combinatorics). So this needs its **own 5-card evaluator**, separate
from the 3-card evaluator used by Teen Patti/3CP. Ace high or low for straights (A-2-3-4-5 and
10-J-Q-K-A valid; no wrap).

- **Minimum paying hand is the defining knob, set by variant:** Jacks-or-Better = pair of J+;
  Deuces Wild = three-of-a-kind (no pair pays); Joker "Kings or Better" = pair of K+.
- **Wild cards** (deuces, joker) substitute for any card. A **wild royal** pays less than a
  **natural royal**. Wild variants add **five-of-a-kind** and **wild-royal** categories. Wilds
  change the **evaluator + paytable only**, never the loop.

---

## 3. The royal-flush max-bet bonus (special-case this)

Every category pays **linearly in coins** EXCEPT the royal flush, which jumps at 5 coins:

| Coins | Royal payout | Per-coin |
|---|---|---|
| 1–4 | 250 × coins | 250 |
| **5** | **4,000** | **800** |

Max-bet (5 coins) is the qualification for the 800-per-coin royal (and for community jackpots).
Implementation: store the royal as a **non-linear special case**; everything else multiplies
linearly by coins bet. All RTP figures below assume 5-coin play.

---

## 4. Variants + paytables + RTP (all config/data, never hardcoded)

The shorthand (e.g. **"9/6"**) names the **full-house / flush** payouts per coin — the two cells
casinos shave to set RTP. **Never infer RTP from a game name** — the same name spans ~95%–100%+.

| Variant (paytable) | Key lines | **RTP @ max bet, optimal** |
|---|---|---|
| **Jacks or Better 9/6** (full-pay) | FH 9 / Flush 6 | **99.54%** |
| Jacks or Better 8/5 (common short) | FH 8 / Flush 5 | 97.30% |
| Bonus Poker 8/5 (full-pay) | quads split by rank | 99.17% |
| Double Bonus 10/7 (full-pay) | quads ~2×; two-pair cut to 1 | **100.17%** (player-+) |
| Double Double Bonus 9/6 | aces+kicker quads | 98.98% |
| Double Double Bonus 8/5 (common) | — | 96.79% |
| Deuces Wild full-pay (25-15-9-5-3-2) | 2s wild; min = trips | **100.76%** (player-+) |
| Deuces Wild "Not So Ugly" (NSU) | quads cut 5→4 | 99.73% |
| Joker Poker (Kings+), best | 53-card, joker wild | **100.65%** (player-+) |

- **The RTP dial:** dropping the full-house or flush payout by 1 each swings RTP **~1.1% per step**
  (9/6 → 8/5 = −2.24%), because FH (~1.15%) and flush (~1.10%) are *high-frequency* hands. The royal,
  despite the huge payout, contributes only ~2% — the **frequency-weighted middle lines dominate**
  RTP, not the flashy top. (NSU Deuces is the cautionary tale: it *raises* several top lines but
  cuts the common quad 5→4 and drops from 100.76% to 99.73%.)
- **Bonus-family quad tiers (implementation-critical):** quads pay by rank and (in DDB) by **kicker**.
  DDB headline: **four aces + a 2/3/4 kicker = 400-per-coin**; four aces + 5–K kicker = 160; four
  2s/3s/4s by kicker = 160/80; four 5s–Ks = flat 50. The evaluator must read the **5th card** when a
  four-of-a-kind of rank A or 2–4 is detected.

---

## 5. Strategy & variance (two independent knobs)

- **Optimal strategy is fully computable** (enumerate all deals × 32 holds × draw-EV → max-EV hold;
  this also yields the exact RTP). It's **per-paytable** but deviations within a family are tiny.
  Surface it only as an optional player hint/trainer; the protocol is just the hold mask.
- **Variance differs sharply by variant** (single-hand standard deviation): **Jacks or Better ≈
  4.42** (smooth, frequent small wins — gentle for new players/retention) vs **Double Double Bonus
  ≈ 6.48** (long dry spells + rare big jackpots — exciting, but brutal on small chip stacks).
- **Tune RTP and variance independently:** RTP (the middle hands) sets the long-run **chip burn
  rate**; variance (concentration in rare quads/royals) sets the moment-to-moment **feel**. Pick a
  low-variance default (JoB) for the everyday table and high-variance variants (DDB/Deuces) as
  optional "high-roller" tables.

---

## 6. Win-rate dial for a non-cashable build
Because chips never cash out, the real-casino constraint (RTP < 100%) **doesn't apply** — the
paytable is a **pure, exactly-computable win-rate dial**. Any candidate paytable's RTP is the
deterministic sum over all 2,598,960 deals of the max-EV hold (computable in seconds), so live-ops
can **solve for the cells that hit a target burn rate** rather than guess. You can even ship
**player-positive** tables verbatim for events (full-pay Deuces 100.76%, 10/7 Double Bonus 100.17%,
best Joker Kings 100.65%) to *inject* chips during a promo, then dial back to a sub-100% default to
drain them. All server-side config (rule #1) — never client-trusted.

---

## 7. Multi-hand (the main "more game per session" lever)

Triple / Five / Ten / Fifty / Hundred Play (IGT Game King):
- Player is dealt **one** hand and makes **one** hold. That same hold is **copied across N hands**.
- Each hand **draws from its OWN independent committed deck** (the dealt 5 removed from each). Draws
  are independent across hands; each is separately verifiable (N deck hashes).
- Each hand is **evaluated and paid separately**; wins are summed.
- **Bet = coins × bet-per-hand × N.** Same RTP as single-hand (operators usually pair multi-hand
  with slightly stingier paytables); **higher variance** (hands share held cards → positively
  correlated → synchronized big swings), and far more game per tap.
- **Implementation: N is a pure fan-out parameter** `{1,3,5,10,50,100}` — N committed decks under
  one hold, N evaluations, summed settle. The only structural addition over single-hand.

---

## 8. Social / multiplayer wrappers (bolt onto Phase-1 social)

Because the hand has no opponent, "social" = competing on a **derived score**, not on cards:
- **Tournaments (highest-value wrapper):** a **leaderboard format**, not head-to-head. Every entrant
  starts with the same **free tournament credits** (NOT their real wallet), a **fixed time/hand
  limit**, **locked to max bet**; rank by final score; top ~10–25% win prizes. Pure async/parallel —
  no synchronized table, no matchmaking, no liquidity. Reuses the **already-built leaderboards**:
  scoped free-credit wallet + timer + score aggregation.
- **Community progressive jackpot:** a small fraction of every bet (~1% meter) feeds a shared pool
  paid on a **max-bet royal flush**; reduce the base paytable to fund it. Shared "we're all chasing
  the same number" hook.
- **Missions / daily challenges / events / VIP** (the PROGRESSION_SPEC + Phase-1 social/gifts/chat
  stack) are the sticky-solitaire spine — already built; video poker just plugs in.

---

## 9. Architecture — reuse vs new

**Reused as-is (already in Khela):** provably-fair commit-reveal shuffle; 52-card deck model;
`IWalletService` debit-on-bet / credit-on-settle (idempotent on `CorrelationId`, `FOR UPDATE`,
`BalanceBefore/After`); per-hand settle audit (`GameHandParticipant.HandIndex` → multi-hand
`0..N-1`); two-bucket clean/tainted wallet; `GameDefinition`/`GameCatalog` config registry;
leaderboards/profiles/missions/gifts/chat/presence; device-guest auth; server-authoritative card
rendering. **No multiplayer table, no SignalR table broadcast for gameplay** — a REST round-trip per
deal/draw suffices (push only for the *social* surfaces like a live tournament leaderboard).

**Genuinely new (small, self-contained):**
1. A **hold/draw turn type** (deal → single hold message → draw) — simpler than blackjack's loop.
2. A **5-card evaluator** with wild-card support (Deuces = 2s wild; Joker = 53-card deck).
3. **Per-paytable config** (JoB / Bonus / Double Bonus / DDB / Deuces / Joker), each a hand-rank →
   multiplier table + the non-linear royal row + kicker rules for bonus families.
4. **Optional multi-hand fan-out** (§7).
5. **(Optional, social) tournament container** (scoped free-credit wallet + timer + score → existing
   leaderboard) and **community jackpot meter** (per-bet contribution → shared pool, max-bet royal).

**Wallet:** debit `coins × bet-per-hand × N` on deal; credit summed paytable wins on settle,
idempotent on `(roundId, handIndex)`. **Max-bet (5-coin) gating** for the royal bonus enforced
server-side. Two-bucket wallet honored (no laundering); dual-currency guard — only Chips/Coins
wagered, token never bettable/winnable; **zero new exposure** since there's no pot or peer transfer.

---

## 10. Build order
1. **Single-hand Jacks or Better** — reuses everything; adds only the 5-card evaluator + one paytable
   + the hold/draw turn. Ship this first.
2. **More paytables** (Bonus / Double Bonus / DDB / Deuces / Joker) — config + evaluator wild support.
3. **Multi-hand fan-out** (Triple/Ten/Hundred Play) — the bet-size + engagement lever.
4. **Tournaments** — the social surface, reusing leaderboards.
5. **Community progressive jackpot** — shared-goal hook.

Each step is additive and never touches the wallet's non-negotiable invariants.

---

## 11. Definition of done (single-hand)
- Server-authoritative deal/hold/draw/settle; client only renders + sends the hold mask.
- Provably-fair: deck committed (hash published) before the deal; draws revealed from committed
  positions; player can verify post-hold. Single deck per hand (53 with joker).
- 5-card evaluator correct (**flush > straight**; ace high/low; wild-royal vs natural-royal; five-of-
  a-kind for wild variants; DDB kicker rule).
- Royal flush special-cased (250/coin at 1–4, 800/coin at 5); all else linear in coins.
- Paytables are config/data; per-paytable RTP computable; max-bet gating server-side.
- Settle is one idempotent `(round, handIndex)` paytable lookup; two-bucket wallet honored; token
  never wagerable. `dotnet build` green; deliberate migrations; no NON-NEGOTIABLE weakened.

---

## Sources
- Wizard of Odds — Video Poker hub + per-variant tables & methodology (all RTP/paytable/strategy/
  variance figures): https://wizardofodds.com/games/video-poker/ ; tables under
  /games/video-poker/tables/{jacks-or-better,bonus-poker,double-bonus,double-double-bonus,deuces-wild,
  joker-poker-kings-or-better}/ ; variance appendix /games/video-poker/appendix/3/.
- Multi-hand mechanics + bet×N + variance (n·v + n(n−1)c): https://www.playusa.com/video-poker/triple-play-draw/ ;
  https://wizardofodds.com/ask-the-wizard/video-poker/multi-hand/
- Tournament format (free credits, timed, max-bet, leaderboard, top 10–25% pay):
  https://www.videopoker.com/toc/rules/17/ ; https://www.casinocenter.com/video-poker-tournament-strategy/
- Progressive/community jackpot (~1% meter, max-bet royal, reduced base paytable):
  https://www.casinocenter.com/video-poker-the-progressive-puzzle/
- Provably-fair commit-reveal (deck hash before deal, reveal/verify draws): https://stake.com/provably-fair/implementation
- Social-casino LiveOps/missions/leaderboards: https://ilogos.biz/social-casino-games-what-they-are-and-how-to-build-them-successfully/
