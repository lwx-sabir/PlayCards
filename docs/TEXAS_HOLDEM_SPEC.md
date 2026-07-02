# Texas Hold'em — Research & Build Spec (for the agent)

*Target: `khela/Khela.Game` (.NET 8) backend + `Khela.Play` Unity client. Honors all CLAUDE.md
NON-NEGOTIABLES: server-authoritative, non-cashable Chips/Coins, dual-currency firewall, idempotent
wallet, provably-fair shuffle. No-Limit Hold'em, 6-max cash, is the target.*

---

## 0. Strategic verdict — build this LATE, not next

Texas Hold'em is the **deepest, highest-retention, most socially sticky** card game — and the
**hardest to build by a wide margin**. It compounds *every* hard problem at once:
- the most complex engine (4 betting streets, **side pots**, all-in math, min-raise/reopen rules);
- the **largest collusion + bot surface of any card game** — materially worse than Teen Patti, and
  the dominant *ongoing operational* cost of running poker;
- the **worst table-liquidity / cold-start problem** — poker fragments players across stake tiers
  and table types, and *"nothing kills conversion like an empty lobby."*

A small Bengali/South-Asian launch audience is the **worst-case** starting condition for poker: too
few concurrent players to fill multiple stakes, and empty tables actively destroy conversion.

**Recommendation:** do **not** make Hold'em the next game. Sequence it:
1. **Blackjack** (done) — vs-dealer, no liquidity requirement.
2. **Teen Patti** — first PvP/pot game: cultural default for the audience, simpler engine, lighter
   liquidity, fewer stake tiers. **This is where you prove the pot-escrow + collusion infrastructure
   Hold'em will reuse.**
3. **Texas Hold'em — later**, once you have (a) enough concurrency to fill multiple stake tiers,
   (b) a proven idempotent pot/side-pot settle path, and (c) anti-collusion/anti-bot tooling.

This spec exists so that when the time comes, the build is de-risked. Most of it **reuses the Teen
Patti pot-escrow + multiplayer turn engine**; the genuinely new parts are side pots, the 4-street
state machine, and a much heavier anti-cheat layer.

---

## 1. Rules (No-Limit Hold'em)

Server deals, shuffles (provably-fair), tracks all state, validates every action, settles. Standard
52-card deck, one freshly shuffled deck per hand. Model the table as a clockwise ring (seat index
mod N). **Use integer chip units (smallest denomination) throughout** to avoid rounding leaks.

### 1.1 Button & blinds
- **Dealer button** rotates one seat clockwise each hand; button acts **last** post-flop.
- **Small blind (SB)** = first active seat clockwise of button; **big blind (BB)** = next. Both are
  forced live bets posted before cards. **Configure SB and BB as independent amounts — do NOT derive
  SB = BB/2** (real tables run $2/$5, $5/$15, etc.).
- **Heads-up (2 players) — inverted; needs an explicit branch:** the **button posts the SB**, acts
  **FIRST pre-flop** and **LAST post-flop**. The normal order does not hold heads-up — this is a
  top bug source.
- **Moving-button model (recommended online):** button advances, BB always filled, departed players
  skipped, no dead seats. (Casino "dead button" rules are more complex — don't use them.) Returning
  players **wait for the BB or post** missed blinds; never deal a posting player in seats between
  BB and button.

### 1.2 Streets & dealing
`post blinds/antes → 2 hole cards each → PREFLOP bet → (burn) FLOP 3 → bet → (burn) TURN 1 → bet →
(burn) RIVER 1 → bet → showdown`. 3 burns total (advance the deck pointer per burn for provable-fair
ordering). Deck usage = `2N + 8`. If a bet goes uncalled (all fold), the hand ends immediately.

### 1.3 Betting rounds & action order
- **Pre-flop:** first to act = seat left of BB ("UTG"); **BB acts last and holds the "option"**
  (if all just called, BB may check to close or raise to reopen).
- **Post-flop:** first to act = first active seat left of the button.
- **Actions:** CHECK (only if nothing owed), BET, CALL, RAISE, FOLD, ALL-IN.
- **Min-raise (NL):** opening bet ≥ BB; `minRaiseTo = currentBet + lastFullRaiseSize`. First raise
  pre-flop is to ≥ 2×BB.
- **Online: require a raise as a single atomic total amount** (sidesteps string-bet ambiguity).

### 1.4 Limit types
- **No-Limit (ship this):** bet any amount from the min up to your whole stack; no raise cap.
- **Pot-Limit (later):** max raise = your call + pot after the call (`M = 3L + T + S`).
- **Fixed-Limit (later):** fixed increments (small bet pre-flop/flop, big bet turn/river), raise cap
  (bet + 3 raises), cap lifted heads-up.

### 1.5 Showdown
- **Who shows first:** last aggressor on the river; if river checked through, first active seat left
  of button. All-fold → last player wins, need not expose.
- **Best 5 of 7:** any combination of the 2 hole + 5 community (may use 2/1/0 hole cards — "playing
  the board"). Enumerate all `C(7,5)=21` subsets, take the max. **Cards speak** — server evaluates
  by computed rank, never by player declaration.
- **Ties split** the pot evenly; each pot (main + side) is evaluated and split **separately**.

### 1.6 Hand rankings (standard 5-card)
Straight Flush (8) > Four of a Kind (7) > Full House (6) > Flush (5) > Straight (4) > Three of a Kind
(3) > Two Pair (2) > One Pair (1) > High Card (0). **Flush beats straight** (normal poker order —
opposite of the 3-card games; this is the **same 5-card evaluator video poker needs**). Tie-break by
ordered comparison vector (e.g. quad rank then kicker; pairs then kickers high→low). **Suits never
break a hand tie** in Hold'em. **Ace plays high (A-K-Q-J-10) and low (A-2-3-4-5 wheel, top card 5);
straights do NOT wrap** (Q-K-A-2-3 invalid).

---

## 2. Side pots — the #1 source of poker-engine bugs

Protect two invariants — **assert both after every hand, alert on any violation:**
```
CONSERVATION: Σ(contributions this hand) == Σ(pot sizes) == Σ(payouts) (+ rake, which is 0 here)
TERMINAL:     every pot == 0 after settle, and Σ(stacks before) == Σ(stacks after)
```

### 2.1 Layering algorithm ("peel the minimum") — feed it CUMULATIVE per-hand contributions
```
contributed[p] = total chips p put in this hand (ALL streets)   # not per-street — classic bug
contenders     = players who reached showdown (did NOT fold)    # folded players fund but never qualify
pots = []
while any contributed[p] > 0:
    layer = min(contributed[p] for p where contributed[p] > 0)
    potAmount = 0; eligible = []
    for p where contributed[p] > 0:
        contributed[p] -= layer; potAmount += layer
        if p in contenders: eligible.append(p)
    remaining = [p where contributed[p] > 0]
    if len(remaining) == 1:                    # excess is UNCALLED → return it, no 1-player pot
        returnUncalled(remaining[0], contributed[remaining[0]]); break
    pots.append({amount: potAmount, eligible})
# pots[0] = main pot, rest = side pots; optional: merge adjacent pots with identical eligible lists
```

### 2.2 Worked example (A all-in 25, B all-in 50, C all-in 100)
- Layer 25 → **Main = 75** {A,B,C}; remaining B=25, C=75.
- Layer 25 → **Side1 = 50** {B,C}; remaining C=50.
- One player left → **50 uncalled, returned to C.** Check: 75+50+50 = 175. ✓
Award main first, then each side pot; per pot the winner is the **best hand among that pot's eligible
players only**; tie → split that pot. **Folded players' chips stay in the pots they matched but they
can never win.**

### 2.3 The short-all-in reopening rule (the single most-violated rule)
In **NL/PL**, an all-in that is **less than a full raise** does **NOT reopen** betting:
- players who **already acted** may only **call or fold** (not re-raise);
- players who have **not yet acted** keep full options.
- A short all-in updates `amountToCall` but does **NOT** change `lastFullRaiseSize`/`minRaise`.
Keep a per-seat `hasActedSinceLastFullRaise` flag. (Multiple short all-ins that *sum* to a full
raise do reopen.) *Fixed-Limit uses a different half-bet rule — don't apply it to NL.*
**Uncalled bets are returned** to the bettor at round close, **before** pot award (return of own
chips, not winnings).

### 2.4 Odd chip
Smallest denomination; **≤ 1 odd chip per player**; in a button game the odd chip goes to the **first
eligible seat clockwise from the button**. Each pot splits separately.

---

## 3. Betting-round completion (battle-tested predicate)
```
roundComplete = NOT [ (contested OR numActive > 1) AND (firstAction OR toAct != lastAggressor) ]
```
- On BET/RAISE: `lastAggressor = toAct`; reopens action for everyone between.
- On CHECK/CALL: `contested = true`.
- On FOLD/ALL-IN: seat leaves `active`, skipped by the pointer.
- `incrementPlayer` advances clockwise, skipping folded/all-in seats; **ends when the pointer wraps
  back to `lastAggressor`.** This one mechanism handles all-checks-around, a bet on a checked street,
  and re-raises with no special cases. The **pre-flop BB option falls out naturally** (seed
  `lastAggressor = UTG`; if all limp, the pointer reaches BB who gets the option).
- `amountToCall = currentBet − committedThisStreet`. `minRaiseTo = currentBet + lastFullRaiseSize`.

---

## 4. State machine (server drives every transition; client only sends the action for `toAct`)
```
WAITING(≥2 ready) → START_HAND(rotate button) → POST_ANTES[skip if none] → POST_BLINDS → DEAL_HOLE
 → BET(PREFLOP) → COLLECT → DEAL_FLOP → BET(FLOP) → COLLECT → DEAL_TURN → BET(TURN) → COLLECT
 → DEAL_RIVER → BET(RIVER) → COLLECT → SHOWDOWN → AWARD_POTS → END_HAND → WAITING
```
**Auto-skip predicates:** dealing bypassed if ≤1 player remains; **betting bypassed if all-but-one
are all-in** (deal remaining streets with no betting → showdown); showdown bypassed if uncontested
(award directly); all-in pots **force a show** (anti chip-dumping).

**Per-seat state:** `status ∈ {ACTIVE, FOLDED, ALL_IN, SITTING_OUT, EMPTY}`; `stack`;
`committedThisStreet` (reset at collection); **`committedThisHand`** (load-bearing for side pots +
conservation); `holeCards`; `autoAction` (the disconnect/timeout hook). Stack→0 transitions
ACTIVE→ALL_IN, leaves rotation, stays eligible up to `committedThisHand`.

---

## 5. Pot as escrow ledger (reuse the Teen Patti pattern)
Model the pot as its own **escrow account** `hand:{handId}:pot`, moved via balanced double-entry:
- **Bet/blind/raise:** `Debit user:balance N / Credit hand:pot N` (idempotency key
  `bet:{handId}:{seat}:{street}:{actionSeq}`).
- **Payout:** `Debit hand:pot N / Credit winner:balance N`, **one leg per (pot × winner)**
  (idempotency key `payout:{handId}:{potId}:{winnerSeat}`).
- Pot drains to **zero** at settle (else it's a conservation bug → alert). Reuse `IWalletService`
  (idempotent on `CorrelationId`, `SELECT … FOR UPDATE`, signed-delta, `BalanceBefore/After`).
- **Two-bucket wallet honored** (PROGRESSION_SPEC §6): chaal/bet debits draw earned-first and record
  their gifted slice; pot payouts keep the pot's aggregate gifted ratio (gifted can't launder
  through a pot). XP basis = winner's clean net.

---

## 6. Timeout / reconnect (generalize the `BlackjackRoundDriver` tick)
- Per-turn pipeline: **action timer (~10–30s) → time bank (~30–60s reserve) → default action**.
- Default = **auto-check if nothing owed, else auto-fold.** Inject the timeout action through the
  **same `actionTaken` path** as a real action so the state machine stays uniform.
- **Disconnect:** pot-scaled reconnect grace (~30s small pot → ~240s large pot); then auto-check/
  fold; prolonged → sit-out. **Do NOT implement legacy "all-in on disconnect"** (abusable, abandoned).
- Reconnect rehydrates from **server-authoritative** board + wallet state, never client-reported.

---

## 7. Anti-cheat — the dominant operational cost (worse than Teen Patti)
Even with non-cashable chips, collusion/chip-dumping/bots corrupt the economy and leaderboards (and
chip-dumping is a money-laundering rail). Poker's surface is bigger than Teen Patti's: **4 streets =
more information to leak**, whipsawing needs the multi-way pots poker creates, leaked hole cards are
higher-value across a 5-card board, and **tournament chip-dumping is poker-native**.

Forms: **collusion** (soft-play, whipsawing/squeezing a victim, signalling hole cards), **chip-
dumping**, **bots / RTA** (live GTO-solver use), **multi-accounting / ghosting**.

Detection (layered; build in this order): **device/IP/fingerprint correlation at seat time** (reuse
Khela's `deviceUniqueIdentifier` — block correlated devices from the same table, near-free) →
**win/loss network graph** (one-sided transfer pairs = chip-dumping; co-occurrence above chance) →
**timing analysis** (bot/RTA tell) → **pairwise VPIP/PFR + conditional-behavior tests** (does A fold
strong hands when confederate B is strong? — the strongest collusion signal, modeled as a Bayesian
network) → **solver-board correlation** for RTA. Remediation norm: **freeze & redistribute** cheater
balances to victims, never keep them.

**The real RNG threat is insider hole-card access, not the shuffle** (Absolute Poker / UltimateBet
superuser, ~$20M). Enforce strict server-side hidden-state isolation + tamper-evident audit logs; the
provably-fair commit-reveal hash chain (you already have one) counters the "is it rigged?" perception
but does NOT cover collusion/insider access — those need the detection layer above.

---

## 8. Formats & monetization
- **Ship 6-max cash (ring) first** — the modern online standard, frequent action, only 6 seats to
  feel "full" (easiest to fill/seed). Defer **Sit-N-Go** and **MTT** (blind-timer escalation, table
  balancing, payout curves, guaranteed prize pools — a large separate build).
- **Take NO rake.** Social model is **IAP chip-packs + VIP** only; rake adds nothing on non-cashable
  chips and blurs the legal line. (Real poker's whole business is rake; you deliberately skip it.)
- Liquidity: lean hard on auto-match-by-stake + seat-fill; consider disclosed/managed bots to seed
  early tables. This is poker's defining launch risk — do not open more stake tiers than your
  concurrency can fill.

---

## 9. Legal posture
- Poker is **more defensible as skill** than Teen Patti (Indian courts lean skill; a US federal judge
  called Hold'em predominantly skill — though reversed on other grounds). **But "skill = legal" does
  NOT exempt real-money operation**, and India's **Aug-2025 federal ban** on real-money online games
  swept in RMG poker. **Stakes override skill** across the region.
- **Bangladesh:** real-money gambling is illegal (Public Gambling Act 1867 + 2025 Cyber Security
  Ordinance; selling poker chips for money treated as gambling). **Non-cashable social is the only
  legal path** — and means zero real-money upside; all revenue is IAP entertainment spend. Lawyer
  review before launch (CLAUDE.md rule #4), specifically on framing (avoid "betting / win money").

---

## 10. Build order (when poker's turn comes)
1. **6-max NL cash engine:** state machine (§4) + betting-round predicate (§3) + **5-card evaluator**
   (shared with video poker) + **side-pot layering** (§2) + escrow settle (§5) + timer/reconnect (§6).
   Test side pots and the short-all-in rule **exhaustively** — they are the bug magnets.
2. **Anti-cheat v1:** device-correlation at seat + win/loss graph + timing (§7). Before opening real
   stakes to a population.
3. **Liquidity:** auto-match, seat-fill/seeding.
4. **Later:** Pot-Limit/Fixed-Limit; Sit-N-Go; MTT (blind structures, table balancing, payouts);
   run-it-twice (EV-neutral, cash-only, defer).

---

## 11. Definition of done (6-max NL cash)
- Reuses the multiplayer table/turn/SignalR infra and the Teen Patti pot-escrow pattern; no parallel
  netcode. Server-authoritative; client only renders + sends the action for `toAct`.
- Side pots correct (peel-the-minimum on cumulative contributions; folded chips fund but don't win;
  uncalled returned; odd chip clockwise-from-button). Conservation + terminal invariants asserted
  after every hand with residual alerts.
- Short-all-in non-reopening rule correct (`hasActedSinceLastFullRaise`, `minRaise` unchanged).
  Heads-up branch correct. Pre-flop BB option correct. Ace-low wheel, no wrap.
- Pot is an escrow account; per-action and per-(pot,winner) idempotency keys; integer chip units.
  Two-bucket wallet honored; token never wagerable.
- Timer→time-bank→auto-check/fold; pot-scaled reconnect grace; no all-in-on-disconnect.
- Anti-cheat v1 live before real stakes. `dotnet build` green; deliberate migrations; no
  NON-NEGOTIABLE weakened.

---

## Sources
- Rules: Robert's Rules of Poker v11 (https://www.pagat.com/docs/RobsPkrRules11.pdf); Wikipedia
  Texas hold 'em / Betting in poker / List of poker hands / Showdown; PokerNews; Upswing.
- Side pots / all-in / state machine: pokernews.com/pokerterms/side-pot.htm; pagat.com/poker/rules/
  betting.html; poker.fandom.com/wiki/Reopening_the_betting; PokerKit (ar5iv 2308.07327);
  github.com/claudijo/poker-ts; github.com/JankoDedic/poker.
- Ledger/idempotency/conservation: bettoblock.com/build-poker-transaction-engine-pot-sidepot-
  management; stripe.com/blog/idempotency.
- Anti-cheat: guardianstack.ai (collusion detection); GTO Wizard Fair Play; partypoker integrity;
  arXiv 2410.07091 (GNN collusion); GGPoker/PokerStars enforcement reports.
- Formats/rake/RNG: pokernews.com; GLI-19 / iTech Labs; CoinPoker provably-fair; acrpoker.eu (PF
  limits).
- Market/legal: Zynga Poker (zynga.com/corporate/zyngapoker15, 236M lifetime players); Sensor Tower
  (WSOP/Zynga WAU+revenue); Pokerfuse (liquidity, India 2025 ban); thedailystar.net (Bangladesh).
