# Social Casino Game + Revenue-Backed Token — Master Plan

*Last updated: 2026-06-09*

> **⚠️ SUPERSEDED (2026-06-19).** This is an earlier draft. The current authoritative strategy doc
> is **`docs/PROJECT_PLAN.md`** (updated 2026-06-19); the terse must-follow rules live in
> **`/CLAUDE.md`**. The strategy below is still directionally correct, but its **§0 "Current state"
> is stale** — the wallet is now wired into gameplay (debit-on-bet), naturals pay 3:2, the blackjack
> vertical slice is playable, and the layout is `khela/Khela.Game` + `khela/Khela.Play` (not
> `khela/PlayCards/*`). Kept for history; do not use for current status.

> **The product:** a free-to-play social casino game (blackjack, poker, roulette
> vs. AI / other players) using non-cashable in-game chips. Real money comes from
> in-app purchases. A **separate, tradeable token** captures a slice of that
> revenue via buy-and-burn — it is *not* the wager chip.
>
> **The core discipline:** build a fun game → prove people pay → grow revenue →
> launch the token only once revenue backs it. The game is the engine; the token
> is the turbocharger you bolt on after the engine runs.

---

## 0. Current state — the Khela codebase, and the path to first revenue

*Reviewed 2026-06-09 at `D:\Projects\PlayCards\khela`.*

**You are already inside Phase 0 with a strong foundation.** What exists:

- **Backend** (`Khela.Game`, ASP.NET Core .NET 8): JWT auth + ASP.NET Identity,
  device registration (mobile-ready), Redis for table state, Swagger, SignalR.
- **Game engine** (`CradGames`): a complete, server-authoritative **blackjack**
  implementation — deal, hit, stand, double-down, split, insurance, dealer
  soft/hard-17 logic, turn timers with auto-stand, multi-seat tables, settlement.
- **Real-time** (`BlackjackHub` + `BlackjackTableManager`): table groups, board
  snapshots broadcast to players, Redis-persisted tables with TTL.
- **Economy rails** (`PlayerWallet`, `WalletTransaction`, `StoreItem`): a
  production-grade ledger — per-user/per-currency wallets with `RowVersion`
  optimistic concurrency, an idempotent transaction log (`CorrelationId`,
  `BalanceBefore/After`, status lifecycle), and an IAP catalog. `CurrencyType`
  already includes `Chips, Coins, Gems, Tokens`.
- **Client**: a Unity project (`PlayCard`) scaffolded (account/save/core).

**The gap that stands between you and money (in priority order):**

1. **Gameplay is NOT wired to the wallet ledger.** Bets/wins currently mutate an
   in-Redis `Player.Balance` (`PlaceBet`, `AddWin`, `AddLoss`, `SettleRound`),
   but nothing debits/credits the persistent `PlayerWallet` via idempotent
   `WalletTransaction`s. This is the integrity backbone — without it, balances are
   spoofable and not durable. **Build this first.** Debit Chips on bet, credit on
   win/push, keyed by `RoundId`/`CorrelationId`, using the existing `RowVersion`.
2. **No purchase flow.** `StoreItem` exists, but there's no store/purchase
   controller and **no server-side IAP receipt validation** (Apple/Google). This
   is literally the "take money" step — until it exists, revenue is $0.
3. **Players are seated with a client-supplied balance**, not their authenticated
   wallet balance. Seat from the DB wallet so bets can't be faked.
4. **Unity client** needs to connect to the hub, render the table, run the
   buy-chips + play loop. It's the thinnest layer right now.

**Smaller fixes:** natural blackjack should pay **3:2** (settlement currently pays
2x / 1:1 on a natural); double-nested `namespace CardGames.Blackjack` in
`BlackjackGame.cs`; add a server-side balance check in `PlaceBet`.

**Legal-critical enforcement (do this in code, now, while it's cheap):** the wallet
is generic across currencies and `TransactionType` has `Bet`/`Win`. Enforce that
**only `Chips`/`Coins` are ever wagerable** and that `Tokens` can **never** be bet
or won at a table. If a tradeable token can be won by gambling, you've built
real-money gambling. Put this rule at the wallet/engine boundary.

**Punch-list to your first paying player (no blockchain):**
1. Wallet-integration service: atomic debit-on-bet / credit-on-settle with idempotency.
2. `StoreController` + IAP receipt validation → credit Chips via `WalletTransaction`.
3. Wallet/balance API; seat players from real wallet balance.
4. Unity: hub connection, table UI, buy-chips, play loop.
5. Ship to a small audience, measure retention + whether strangers pay.

**Token work stays deferred** until the above earns real, growing revenue (Phase 2).

---

## 1. The bet, in one paragraph

Social casino games are a proven, legal, multi-billion-dollar industry (Zynga
Poker, Slotomania): players pay real money for virtual chips they can never cash
out, purely for entertainment. That business stands on its own. On top of a
*working, revenue-generating* game, a revenue-backed token turns proven cash flow
into something tradeable and ownable — the legitimate, value-anchored kind of
token (Helium/Render model), not a memecoin or a gambling instrument. Most of the
risk and reward lives in whether the *game* succeeds; the token is a multiplier on
that success, never a substitute for it.

---

## 2. The non-negotiable architecture: two separate currencies

This is the single most important design decision. Merging them is illegal;
separating them is the whole game.

### 2.1 In-game chips — NOT a token, no cash-out
- Ordinary virtual currency, tracked in a normal database (no blockchain).
- Bought with real money (in-app purchase) and/or earned through play/daily bonuses.
- **Cannot be withdrawn, traded, or converted to money or to the token.**
- This is what keeps you inside legal *social casino* territory rather than
  *real-money gambling*. Chips have no real-world value, so wagering them is not
  legally "gambling."

### 2.2 $TOKEN — the tradeable, revenue-backed coin
- A standard ERC-20 on Base, traded on a DEX. Separate from gameplay.
- Its value comes from the **game's revenue**, not from being wagered.
- **Players do NOT win or earn $TOKEN at the tables.** (If they could, winning
  tokens-with-market-value by gambling would re-create real-money gambling — the
  exact trap we're avoiding.)
- Holders benefit because a share of revenue is used to buy and burn $TOKEN.

### 2.3 Why this separation is mandatory
| If you merge them (token = wager chip) | If you separate them (this plan) |
|---|---|
| Buy token → gamble → win cashable tokens = **real-money gambling** | Chips are valueless play money = **legal social casino** |
| Needs a gambling license; illegal in US, Bangladesh, many places | No gambling license needed for the game |
| Profit-bearing wager token = likely a security too | Token is revenue-backed, fair-launched, separate |
| Players drained by house edge get angry / harmed | Players pay for entertainment, knowingly; token holders separate |

---

## 3. The token model (buy-and-burn)

**Start with buy-and-burn**, not staking/revenue-share — it's simpler and carries
lower securities risk.

- Commit a fixed, public % of net IAP revenue (e.g. **20%**) to a buyback wallet.
- On a schedule, that wallet **buys $TOKEN on the open market and burns it**
  (sends to a dead address; permanently removes it from supply).
- Effect: continuous demand (buys) + shrinking supply (burns). As revenue grows,
  buybacks grow, and each token is a larger share of a smaller pie.
- Everything on-chain and verifiable. You describe the **mechanism**; you never
  **promise** the price goes up (legal + honest line).

**Hard rules:**
- Buybacks are funded ONLY by real external revenue (players buying chips). Never
  fund buybacks by selling more tokens — that's a Ponzi.
- No fake yield (paying holders with newly minted tokens = inflationary death spiral).
- Transparency is the trust: publish the buyback wallet and every burn tx.

**Later option (Phase 4+, with a lawyer):** real-yield staking — distribute a % of
revenue to stakers in USDC. Stronger holder pull, but looks more clearly like a
security. Don't start here.

---

## 4. What the token is actually worth (revenue → value)

The token prices on *expected future* revenue, but here's the fundamental anchor at
different scales (illustrative, ~20% buyback, not promises — early prices swing
wildly on hype):

| Game revenue / mo | Buyback / yr | Rough token mcap | Your ~15% stake |
|---|---|---|---|
| $1k | $2.4k | $25k–50k (a toy) | $4k–8k |
| $10k | $24k | $250k–500k | $40k–75k |
| $100k | $240k | $2.5M–5M | $375k–750k |
| $1M | $2.4M | $25M–50M | $4M–7M |

**Reading this table is the strategy.** At $1k/month flat the token is a micro-cap
toy not worth launching. The token becomes real money only as the game scales —
and the market rewards the *growth rate*, not the snapshot. Your first paying
players matter as **validation** (proof strangers pay), not as income.

---

## 5. Sequencing — the part that prevents wasted months

Most game/token projects die by building the expensive part before validating the
cheap, decisive one. Do it in this order:

**Phase 0 — Prove it's fun and people pay (weeks, cheap). NO token, NO blockchain.**
- Build a lean playable version of ONE game (start with blackjack or slots — simplest).
- Get it in front of real players. Add basic IAP.
- Decisive question: **will strangers pay real money to keep playing?**
- If no after honest effort → stop here. You've saved months for the price of weekends.

**Phase 1 — Grow revenue ($1k → $10–50k/mo). Still no token.**
- Improve retention, add games, tune monetization, find a user-acquisition channel
  that isn't more expensive than the revenue it brings.
- This is a normal, legal, profitable game business on its own.

**Phase 2 — Launch the token (only once revenue backs it).**
- Deploy $TOKEN + buyback contract on Base. Fair launch, transparent founder vesting.
- Wire game revenue → buyback → burn, all public.
- Now there's real cash flow to anchor value and a real player community for distribution.

**Phase 3 — Compound.**
- Scale revenue → bigger buybacks → stronger token → more attention → more players.
- Cross-promote with BitcoinRiser's audience/content for distribution.

**Phase 4 — Optional.**
- Real-yield staking, governance, multi-game platform — each with its own legal review.

---

## 6. Technology

**Game client:** **Unity (C#)** — plays to your .NET background, ships to iOS,
Android, and PC/web from one codebase, and has ready-made casino kits. (Godot or a
web/Phaser stack are alternatives; Unity is the pragmatic pick here.)

**Backend:** **ASP.NET Core** (your strength, reuse BitcoinRiser patterns) for
accounts, the chip ledger, matchmaking, anti-cheat, and **server-side IAP receipt
validation** (Apple/Google). Chips live in the DB, never on-chain.

**RNG / fairness:** server-authoritative RNG for house games; consider a
provably-fair scheme as a trust feature.

**Token layer (Phase 2+):** ERC-20 + buyback contract on **Base**; a treasury the
game backend funds from net revenue; buybacks executed via a DEX router; audit
before mainnet. Kept entirely separate from the chip ledger.

---

## 7. Competitive strategy / moat

You won't out-spend Zynga or established crypto-casinos. Win on a wedge:

1. **Niche/region first** — e.g. a South Asian / Bengali-facing social casino +
   community (in-language, local channels), where global incumbents don't bother.
   Own a small pond completely before expanding.
2. **BitcoinRiser as a distribution engine** — your existing content/SEO platform
   drives players and (later) token attention for near-zero marginal cost. This is
   the asymmetry funded competitors lack.
3. **Trust + transparency** — provably-fair games and an on-chain, verifiable token
   model differentiate from both sketchy crypto-casinos and faceless incumbents.

---

## 8. Legal & compliance checklist (do before taking real money)

- **Keep chips non-cashable and non-convertible.** This is what makes the game a
  legal social casino rather than gambling.
- **Never let players win $TOKEN through play.** Token is bought/sold on exchanges
  only; gameplay never pays out anything of market value.
- **App store rules:** Apple/Google have specific policies for casino-themed games
  (age-gating, geo-restrictions, no real-money gambling). Plan for them.
- **Gambling law is jurisdiction-specific and criminal to get wrong** — get a
  gaming/gambling lawyer's sign-off before launch.
- **Securities:** buy-and-burn token, fair launch, no profit promises; lawyer review
  under the 2026 CLARITY-Act framework before any sale.
- **Your jurisdiction:** crypto is restricted in Bangladesh — structure the token/
  company entity accordingly (likely offshore) and separate it cleanly from the
  game so the game isn't classified as crypto gambling.
- *This document is information, not legal or financial advice.*

---

## 9. Costs

| Item | Estimate | Notes |
|---|---|---|
| Phase 0 game MVP | mostly your time | Unity + assets; minimal cash |
| IAP / store accounts | ~$100–125 | Apple $99/yr, Google $25 one-time |
| User acquisition (Phase 1) | variable | Must stay below revenue per user |
| Token contracts + audit (Phase 2) | $5k–30k+ | Only when revenue justifies it |
| Legal review | varies | Gambling + securities; budget for it |

Downside is bounded: Phase 0/1 is a normal game business; the token spend only
happens after the game is already earning.

---

## 10. Honest risks

- **Most games get no traction.** Fun and retention are hard; the token can't save
  a game nobody plays.
- **Social casino is competitive** and user acquisition can be costly — the niche +
  BitcoinRiser distribution is how you keep CAC sane.
- **The token adds legal complexity** (gambling + securities). Sequencing and the
  dual-currency split contain it, but a lawyer is non-optional.
- **At small revenue the token is a toy** — it only matters if the game scales.

The mitigations are baked into the plan: validate cheap before building expensive,
keep the two currencies separate, launch the token only when revenue backs it.

---

## 11. Immediate next steps

1. Pick the first game (blackjack or a slot — simplest to build and monetize).
2. Build a lean Unity MVP with a chip ledger on an ASP.NET Core backend. No token yet.
3. Add basic IAP and get it in front of real players.
4. Measure: retention + whether strangers pay. That's the go/no-go gate.
5. Only after revenue is real and growing: design the token launch (separate doc).

---

### Related docs
- `bitcoinriser/docs/LAUNCHPAD_PLAN.md` — the token-launchpad idea, **parked** as an
  earlier option. BitcoinRiser still serves here as the content/distribution engine.
