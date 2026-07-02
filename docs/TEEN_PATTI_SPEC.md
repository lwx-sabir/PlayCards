# Teen Patti — Research & Build Spec (for the agent)

*Target: `khela/Khela.Game` (.NET 8) backend + `Khela.Play` Unity client. Honors all CLAUDE.md
NON-NEGOTIABLES: server-authoritative, non-cashable Chips/Coins, dual-currency firewall, idempotent
wallet, provably-fair shuffle. Teen Patti is the second game — a multiplayer, turn-based POT game.*

---

## 0. Framing — what's actually new

Khela is a multiplayer game. **The blackjack table is already multiplayer and turn-based** — shared
table state in Redis, SignalR push to all seats, seat management, and the `BlackjackRoundDriver`
tick that drives turns and auto-actions on timeout. Teen Patti **reuses all of that**. It is NOT a
new netcode project.

What Teen Patti genuinely adds over blackjack is a short, specific list, and it all stems from one
fact: **in Teen Patti players win each other's chips (a shared POT), not the house's.** That single
change drives every new piece:
1. **A shared pot** (escrow + side pots) — money moves between players, so settlement is multi-party,
   not the two-party player↔house credit/debit blackjack uses.
2. **PvP showdown** instead of vs-dealer comparison.
3. **Blind/seen (chaal) stake mechanics** — an asymmetric current-stake transform.
4. **Side-show** — a private request/accept sub-state between two players.
5. **A collusion surface** — because you can lose to a confederate on purpose, which doesn't exist
   when everyone plays the house.

Everything else (server-authoritative state, turn engine, SignalR, timer-driver, provably-fair
shuffle, idempotent wallet, seat/lobby/auto-match) is **already built and reused as-is**.

Teen Patti is the right second game for the Bengali/South-Asian market: it's the culturally
dominant card game (Diwali tradition, family-table game; two Teen Patti titles sit in India's top-5
grossing apps), and it's a **multiplayer social** game — exactly the engagement/retention shape
Khela is built around (long sessions, table presence, whale tables), unlike a solitary format.

---

## 1. Rules (implementation-grade)

Standard 52-card deck, no jokers in the base game. Aces high, twos low. **Server deals, shuffles
(provably-fair), tracks all state, validates every action, settles** — client only renders + sends
actions (CLAUDE.md rule #1), same as blackjack.

### 1.1 Round flow
1. **Boot/ante:** every seated player posts an equal minimum stake (the *boot*, the smallest unit)
   into the central **pot** before the deal.
2. **Deal:** 3 cards face-down to each player.
3. **Turn order:** starts left of the dealer, clockwise, looping for as many betting rounds as
   needed.
4. On your turn you either **bet into the pot to stay** or **pack (fold)** (forfeit contributions).
5. **Round ends** when either (a) **all but one fold** → lone survivor takes the pot, no reveal; or
   (b) **two players remain and one pays for a SHOW** → hands compared, higher wins. **A show is
   always heads-up — there is no multi-way showdown.**

### 1.2 Blind vs Seen (chaal) — the core stake math
Each player is **blind** (hasn't looked) or **seen/chaal** (has looked; one-way transition). Both
reference a running **current stake** (starts at the boot = 1 unit):

- **Blind player** stakes **1×–2× the current stake**. After a blind bet, **next player's current
  stake = the amount the blind player put in.**
- **Seen player** stakes **2×–4× the current stake**. After a seen bet, **next player's current
  stake = HALF the seen player's bet.**

So a seen player always pays double a blind player for the equivalent level. **Enforce even-amount
bets while any blind player remains** (so the seen→half transform stays integral). The transform is
asymmetric and **computed server-side — never trusted from the client.**

### 1.3 Show (showdown) — heads-up only
- Legal only when **exactly two players remain**.
- **Blind caller:** cost = current stake (1×); doesn't look until after paying.
- **Both seen:** either may pay **2× current stake** for a show.
- **A seen player may NOT demand a show against a blind opponent** (can only keep betting or fold).
  A blind player *can* show against anyone. **Enforce server-side.**
- **Tie rule — config flag:** modern app default = **split pot** on exact tie; classic = non-caller
  wins. Ship **split pot** as default.

### 1.4 Side-show / back-show (private compare)
- **Requester:** a **seen** player, against the **immediately previous** player, who must also be
  **seen**. Blind players can neither request nor be asked. Requires **3+ players** still in.
- Requested **immediately after placing your bet** (the seen amount, 2× current stake, covers it).
- Previous player **accepts or declines**. Accepted → private compare, **lower hand folds**; on a
  **tie the requester folds**. Declined → play continues, requester stays in.
- Needs a dedicated **server-mediated sub-state**: `request → pending → accept/deny → fold-loser`,
  distinct from the normal bet loop, with **cards revealed only to the two parties.**

### 1.5 Pack & timeout
- **Pack (fold):** drop out, forfeit contributions.
- **Turn timer → auto-PACK** on expiry (configurable per table; common tiers ~7/15/30/60s). This is
  the Teen Patti analog of blackjack's auto-stand — but auto-fold is **consequential** (forfeits the
  player's pot stake), so add a short **reconnect grace window** before it commits.

### 1.6 Hand rankings (best → worst) + tie-breaks
| Rank | Name | Definition | Tie-break |
|---|---|---|---|
| 1 | **Trail / Trio** (3 of a kind) | 3 same rank | higher rank; **A-A-A high, 2-2-2 low** |
| 2 | **Pure Sequence** (straight flush) | 3 consecutive, same suit | sequence order (below) |
| 3 | **Sequence / Run** (straight) | 3 consecutive, mixed suits | sequence order (below) |
| 4 | **Colour** (flush) | 3 same suit, non-consecutive | high card, then 2nd, then 3rd |
| 5 | **Pair** | 2 same rank + kicker | pair rank, then kicker |
| 6 | **High Card** | none of the above | high, then 2nd, then 3rd |

- **Any higher category beats any lower outright** (lowest run 4-3-2 beats best flush A-K-J).
- **Straight ranks ABOVE flush** (pure sequence > colour) — inverse of 5-card poker, and
  **mathematically correct**: in 3 cards a straight (720 combos) is rarer than a flush (1096).
  **Write a dedicated 3-card evaluator — do NOT reuse a 5-card straight/flush ordering.**
- **Sequence ordering — config flag:** default **A-K-Q highest, then A-2-3, then K-Q-J … down to
  4-3-2**. `A-2-3 outranks K-Q-J` is **universal — implement with confidence**; only A-2-3-vs-A-K-Q
  for the top spot varies by convention. `2-A-K` is **not** a valid run.
- **No suit tie-break** by default (exact ties → split pot per §1.3).

### 1.7 Variants — what to ship (same engine, eval tweak only)
1. **Classic** (above).
2. **Muflis** (lowball — rankings fully inverted; trivial: flip the comparator). Most popular variant.
3. **AK47** (A/K/4/7 wild) **or** a generic **Joker** mode (a revealed rank is wild). Very common.
- Later/optional: Best-of-Four (deal 4, best 3 — eval unchanged, deal differs), 999, Lowest Joker.

---

## 2. Concrete config (operator-documented, from Octro — all tunable)

| Table level | Boot | Max blinds | Max Chaal | Pot limit |
|---|---|---|---|---|
| Entry | 2 | 4 | 256 | 2,048 |
| Next | 4 | 4 | 512 | 4,096 |

- Ladder scales geometrically (boot, chaal cap, pot limit all double per tier). Gate higher tiers
  behind chip balance / level (Low/Med/High/VIP routing, like blackjack).
- **Pot-limit → forced showdown:** when the pot limit is hit, all remaining players are forced to
  show; winner takes the pot. This bounds infinite raising — implement it.
- **Max blinds (e.g. 4):** after N blind turns a player is forced to "see."
- **Seats per table: 5–6** (conventional; traditional 3–7, max 10). Reuse the blackjack seat model.
- **Even-amount enforcement** while any blind remains (§1.2).
- **Timer → auto-pack** + reconnect grace.

---

## 3. Architecture — reuse vs new (built on the existing multiplayer engine)

### 3.1 Reused from blackjack as-is (do NOT rebuild)
- **Multiplayer table + seat model**, shared state in Redis, lobby/auto-match by level + open seat.
- **Turn engine + `BlackjackRoundDriver`-style tick** driving turns and auto-action on timeout
  (Teen Patti's default expiry action = **auto-pack** instead of auto-stand).
- **SignalR push** of board snapshots to all seats (+ polling fallback — though real-time matters
  more here since every action mutates shared pot state all seats must see consistently).
- **Provably-fair commit-reveal shuffle** (per-table server seed, hash chain) — used verbatim.
- **Idempotent `IWalletService`** (signed-delta, `SELECT … FOR UPDATE`, dedup on `CorrelationId`)
  and the **two-bucket clean/tainted wallet** (PROGRESSION_SPEC §6).
- Provision a generic `GameDefinition`/`GameCatalog` entry (`teenpatti`) — config, not new routing.

### 3.2 New: the POT as an escrow ledger account
The one real money-model change. Model the pot as a **per-hand escrow account** in the ledger:
- Each bet = transfer **`player → pot:{handId}`** (a debit on the player + credit into the pot
  escrow). Each payout = transfer **`pot:{handId} → winner`**.
- **Conservation falls out for free:** the `pot:{handId}` account must **net to zero** after settle,
  and Σ contributions == Σ payouts. **Alert on any pot residual** (non-zero pot account, or
  contributions ≠ payouts) as a hard settle invariant — same money-audit discipline as blackjack.
- **Disconnect mid-hand is trivially correct:** chips already in the pot escrow stay there; the
  player forfeits them on auto-fold. No dangling debit (the debit already has a matching pot credit).
- Strictly better than an in-memory pot accumulator (which loses the audit trail and needs crash
  recovery). Use the escrow account.
- **Idempotency keys** (extend the blackjack discipline): bets `(handId, roundId, seatId, actionSeq)`;
  payouts `(handId, potId, winnerSeat)`.
- **Two-bucket integrity:** a chaal debit draws earned-first and records its gifted slice; the
  winner's pot credit keeps the **pot's aggregate gifted ratio**, so gifted chips can't be laundered
  through a pot. XP basis = the winner's earned (clean) net.

### 3.3 New: blind/seen state + PvP showdown + side-show
- Per-seat: add a **`blind|seen`** flag and per-seat contributed amount to the existing seat state.
- **Current stake** transform (§1.2) computed server-side.
- **Showdown** compares player hands (3-card evaluator, §1.6), not a dealer hand; pays the pot.
- **Side-show** is the one new interactive sub-state (§1.4): request → pending → accept/deny →
  fold-loser, private reveal to two parties only.
- Build the **new 3-card evaluator** (straight > flush; A-2-3 > K-Q-J; config flags for sequence-top
  and tie-rule).

---

## 4. New: collusion control (the burden that doesn't exist vs the house)

Because players win each other's chips, PvP opens chip-dumping, soft-play, two-accounts-at-one-table,
and ghosting — which corrupt the chip economy and leaderboards **even though chips are non-cashable**
(chip-dumping is also a money-laundering rail). Minimum controls before opening PvP to a population:
- **Device/IP correlation at SEAT time (cheapest, highest value):** Khela already derives identity
  from `deviceUniqueIdentifier` — use that exact signal to **block two correlated devices from
  sitting at the same table**, checked in ms before seating. Necessary but not sufficient (VPNs /
  anti-detect defeat IP/fingerprint at scale); it kills the easy "my own two phones" dumping.
- **Batch win/loss + table co-occurrence graph job:** flag one-sided transfer pairs (A repeatedly
  loses to B) and pairs who meet at the same table far above chance. Defer ML-on-hand-histories.
- **Velocity checks** on account/seat creation per device.
- This is an **ongoing operational cost**, not a one-time build — budget for it before PvP opens.

---

## 5. Legal / fairness posture (must-read before shipping)

- **Teen Patti is judicially riskier than blackjack.** Indian courts specifically classify
  three-card "flush/Teen Patti" as a **game of chance** (unlike rummy/poker = skill). Several states
  ban money play (Assam, Telangana, Andhra, Odisha); India's **PROGA (Aug 2025)** broadly prohibits
  online money games. **Non-cashable social model is mandatory**, enforced even harder than blackjack.
- **Bangladesh** (target audience): real-money gambling is **criminal** — the **Cyber Security
  Ordinance 2025** criminalizes operating/using a gambling app (up to 2 yrs jail / ~Tk 10M fine),
  with ISP blocking + CID enforcement. **Non-cashable, non-transferable chips are the only viable
  structure**; marketing must avoid "betting / win money" framing. **Lawyer review before launch**
  (already mandated) — specifically on Teen Patti framing.
- **Fairness:** the social-Teen-Patti market norm is **certified RNG** (iTech Labs / GLI / eCOGRA),
  not provable fairness. Khela's **provably-fair shuffle exceeds the bar** — a marketing edge.

---

## 6. Build order
1. **`teenpatti` GameDefinition** + table/seat/lobby wiring (reuse blackjack infra) + the **3-card
   evaluator** (§1.6).
2. **Pot-escrow ledger account** + idempotent multi-party settle + conservation/residual invariant
   (§3.2).
3. **Blind/seen turn flow** (current-stake transform, even-amount, max-blinds, pot-limit→forced
   showdown) + auto-pack + reconnect grace (§1.2, §1.5, reusing the tick driver).
4. **Show + side-show** sub-states (§1.3–1.4).
5. **Classic** playable end-to-end → then **Muflis**, then **AK47/Joker** (§1.7).
6. **Anti-collusion v1** — device-correlation at seat time + batch transfer-graph job (§4). **Before**
   opening real PvP to a population.
7. **Legal review** of framing + non-cashable enforcement (§5) before public launch.

---

## 7. Definition of done
- Reuses the existing multiplayer table/turn/SignalR/seat infra; no parallel netcode.
- Server-authoritative throughout; client only renders + sends actions.
- Pot is a per-hand escrow ledger account; settle nets it to zero; Σ contributions == Σ payouts;
  pot-residual alert wired. All bets/payouts idempotent on their composite `CorrelationId`.
- Blind/seen current-stake transform, even-amount enforcement, max-blinds, pot-limit→forced-showdown
  enforced server-side. Show is heads-up-only and seen-can't-show-vs-blind. Side-show sub-state works
  (private compare, lower/tie-requester folds).
- 3-card evaluator correct incl. **straight > flush** and **A-2-3 > K-Q-J**; sequence-top and
  tie-rule are config flags.
- Auto-pack on timeout + reconnect grace. Device-correlation seat check live before PvP opens.
- Two-bucket wallet honored (gifted can't launder through a pot); non-cashable enforced; token never
  wagerable; `dotnet build` green; deliberate migrations.

---

## Sources
- Rules: Pagat (https://www.pagat.com/vying/teen_patti.html), Wikipedia Teen Patti
  (https://en.wikipedia.org/wiki/Teen_patti), Octro Learn
  (https://teenpatti.octro.com/learn-teen-patti/index.html).
- App implementation / config: Octro (boot/blind/chaal/pot caps), Moonfrog Teen Patti Gold rules
  (https://teenpattigold.com/rulesnew.html).
- PvP pot engineering / collusion: bettoblock pot/side-pot engine
  (https://bettoblock.com/build-poker-transaction-engine-pot-sidepot-management/), GuardianStack
  collusion detection (https://blog.guardianstack.ai/online-poker-cheating-detection/).
- Legal: Chambers/AZB India gaming law
  (https://chambers.com/legal-trends/landscape-of-online-gaming-laws-in-india); Bangladesh Cyber
  Security Act 2025 enforcement (Gambling Insider / The Daily Star).
- Market: IBEF/EY India gaming (~$3.7B 2024, RMG 85.7%, ~$9B by 2029); secondary app figures
  (Teen Patti Gold ~6M MAU; Octro 800% paying-user growth 2020) — directional; pull a paid Sensor
  Tower / AppMagic export for board-grade numbers.
