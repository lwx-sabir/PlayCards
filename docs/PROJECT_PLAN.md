# Khela World — Master Project Plan

*Owner: Reza · Last updated: 2026-06-15*

This is the full plan and reasoning for the game project. The terse, must-follow
rules live in `/CLAUDE.md` (read automatically by Claude Code); this document is the
"why" behind them. When the two disagree, `CLAUDE.md` wins for code decisions.

---

## 1. Vision

A free-to-play **3D social casino wrapped in a virtual life**. Players play casino
games (blackjack first, then poker / Texas Hold'em, roulette, slots) using
**non-cashable in-game coins**, and live a light metagame: avatars, apartments they
furnish, goods, and gifting. Real money comes from **in-app purchases** of coins and
virtual items. A **separate, revenue-backed token** comes much later.

Two things make this more than "another card app":
- **The virtual-life metagame** (apartments, goods, gifts) is the retention + spend
  engine that pure card games lack — proven by the big social-casino and avatar titles.
- **A regional distribution wedge** (Bengali / South-Asian audience + diaspora) that
  global incumbents underserve — the one advantage money can't easily buy.

### Guiding principle
The game must be **fun and earn on its own**. The token is fuel bolted on *after* the
game makes real, growing revenue — never a substitute for a game people want to play.

---

## 2. Sequencing (do not skip ahead)

**Phase 0 — Fun + first paying players.** One casino game (blackjack) with a complete,
server-authoritative play loop wired to the coin wallet, plus the IAP purchase flow.
Decisive question: *will strangers pay real money to keep playing?* No virtual-life
system yet, no blockchain.

**Phase 1 — Grow revenue.** Add games (poker, roulette, slots), retention features, and
the virtual-life metagame (apartments, goods, gifting). Tune monetization. Find a
user-acquisition channel cheaper than the revenue it brings. This is a normal, legal,
profitable game business on its own.

**Phase 2 — Token.** Only once revenue is real and growing: launch a separate
revenue-backed token (see §4). Fair launch, transparent vesting, buy-and-burn from real
IAP revenue.

> **Write no blockchain/token code until the game earns real, growing revenue.**

---

## 3. Dual-currency model (the legal backbone)

This is the single most important design decision. Merging the two is illegal; keeping
them separate is what makes the whole thing work.

**In-game coins/chips — NOT a token, no cash-out.** Ordinary virtual currency in MySQL.
Bought with real money or earned through play/bonuses. Used to wager at tables and to
buy goods/apartments/gifts. **Cannot be withdrawn, traded, or converted to money or to
the token.** This is what keeps us a legal *social casino*, not real-money gambling.

**$TOKEN — the separate, tradeable, revenue-backed coin (Phase 2+).** A standard ERC-20
(on Base), traded on a DEX. Its value comes from the game's revenue, **not** from being
wagered. **Players never win or buy goods with the token at the expense of the wager
system, and never win the token by gambling.** If a tradeable token could be won at a
table, we would be running real-money gambling.

**Enforced in code** at the wallet/engine boundary: only `Chips`/`Coins` are wagerable;
`Tokens` can never be bet or won (`WalletService` rejects it).

---

## 4. Token model (Phase 2) — buy-and-burn

Start with **buy-and-burn**, not staking (lower securities risk, simpler):
- Commit a fixed, public % of net IAP revenue (e.g. 20%) to a buyback wallet.
- That wallet buys `$TOKEN` on the open market and burns it. Continuous demand +
  shrinking supply; as revenue grows, buybacks grow.
- Funded ONLY by real external revenue (players buying coins). **Never** by selling
  tokens — that would be a Ponzi.
- Everything on-chain and verifiable. Describe the mechanism; never *promise* price.

**What it's worth (illustrative, ~20% buyback — NOT a promise):**

| Game revenue / mo | Buyback / yr | Rough token mcap | Founder ~15% stake |
|---|---|---|---|
| $1k | $2.4k | $25k–50k (a toy) | $4k–8k |
| $10k | $24k | $250k–500k | $40k–75k |
| $100k | $240k | $2.5M–5M | $375k–750k |
| $1M | $2.4M | $25M–50M | $4M–7M |

The token only becomes real money if the game scales. It's a multiplier on business
success, not a substitute. Founder upside = transparent vested allocation + normal
business profit (the ~80% of revenue not routed to buyback).

Securities note: a profit-bearing token has Howey exposure — fair launch, no profit
promises, lawyer review before any sale.

---

## 5. Architecture

- `khela/PlayCards/Khela.Game` — ASP.NET Core .NET 8 backend: JWT + Identity, MySQL
  (Pomelo EF Core), Redis, SignalR, REST API, Serilog.
- `khela/PlayCards/Khela.Game/CradGames` — game-logic library (blackjack engine).
- `khela/PlayCards/Khela.Common` — DTOs shared between backend and Unity client.
- `khela/PlayCards/PlayCard` — Unity client (URP, fully 3D). Namespaces `PlayCard.*`.

### Networking
- **Actions** (bet/hit/stand/deal/double/split) → **REST** (`BlackjackController`).
- **Live state** → **SignalR hub** (`BlackjackHub` → `TableUpdated` board snapshots).
- Unity transport: **Best SignalR** (mobile/WebGL reliable). All transport behind
  `IBlackjackHubClient` so it can be swapped without touching game code.
- WebGL: token goes as `?access_token=`; server JwtBearer needs an `OnMessageReceived`
  hook to read it from the query string for `/blackjackhub`.

### Non-negotiable engineering rules
1. **Server-authoritative.** Server deals/shuffles/settles; client only renders snapshots
   and sends actions. Client never generates cards, decides outcomes, or holds an
   authoritative balance. Use the card asset pack as a *renderer only*.
2. **Dual-currency guardrail** enforced at the wallet boundary (see §3).
3. **Wallet integrity** — all balance changes via `IWalletService` (idempotent on
   `CorrelationId`, pessimistic `SELECT ... FOR UPDATE`). Chips in MySQL, never on-chain.
   Never trust client-supplied balances.
4. **No real-money gambling, no custody.** Token non-custodial and revenue-backed.

---

## 6. Current state & next steps

**Done:** blackjack engine (hit/stand/double/split/insurance, dealer logic, settlement),
SignalR push, JWT + device auth, Redis table state, hand-history audit tables, and the
**`WalletService` ledger** (idempotent debit/credit, FOR UPDATE locking, wagerable guard,
signed-delta amounts, `RowVersion` drift fixed to `DateTime?`).

**Next (Phase 0 → revenue), in order:**
1. **Wire `WalletService` into bet/settle** — debit coins on `Deal`, credit on settlement;
   record `WalletDebitTxId`/`WalletCreditTxId` on `GameHandParticipant`. (Wallet calls live
   at the controller/hub boundary since `BlackjackTableManager` is a singleton and the
   `WalletService`/DbContext is scoped.)
2. **IAP purchase flow** — `StoreController` + server-side Apple/Google receipt validation
   → credit Chips via `WalletTransaction`. This is the literal "take money" step.
3. **Seat players from their real wallet balance** (not a client-supplied value).
4. **Unity client** — Best SignalR `BlackjackHubClient` (against `IBlackjackHubClient`),
   card renderer, play loop, buy-coins screen.

**Smaller fixes:** natural blackjack should pay 3:2; remove the doubled
`namespace CardGames.Blackjack` in `BlackjackGame.cs`; add a server-side balance check in
`PlaceBet`.

---

## 7. Unity client setup (URP, 3D)

- **Pipeline:** URP. After importing any Built-in-pipeline assets, run the **Render
  Pipeline Converter** (materials show magenta until converted).
- **SignalR:** Best SignalR (Best HTTP + WebSockets + SignalR bundle). Implement
  `BlackjackHubClient` against `IBlackjackHubClient`; JWT via `?access_token=`.
- **Card pack (AarniTuli, itch.io ZIP):** it's a `.blend`-based UPM package (`package.json`
  + asmdef). Install requires **Blender installed**, and must preserve the original
  `.meta`/GUIDs (the broken import lost guid `47aaf…` for `CardBase`). Install via Package
  Manager "Add package from disk" → its `package.json`, or drop the inner package folder
  into `Packages/`. **Use it as a renderer only** — never its local deck/shuffle. Map the
  server's `FaceValue`/`Suit` enums to the pack's `value`/`Suit` (it has `suitToInt()`);
  ideally promote those enums to `Khela.Common` so both sides share one definition.

---

## 8. Naming & store strategy

**Umbrella brand:** *Khela World* (Khela = "play" — a social world of games, apartments,
and gifting). One app, one brand; per-game emphasis lives in ASO, not in separate binaries.
Avoid "Virtual"/"King"/"Master" (generic filler) and the Indian card-gaming trademarks
(Adda52, PokerBaazi, RummyCircle, Junglee) — never name anything "Adda" or "Baazi".

**Single app title** (Apple name + Google default title, both ≤30 chars):
`Khela World: Casino & Slots`, subtitle/short-desc `Poker · Blackjack · Roulette · Life`.
The icon + first 2–3 words + screenshots drive most of the attention.

**Per-game listings — Apple and Google differ:**
- **Google Play Custom Store Listings** CAN change title/icon/description, so you get true
  per-game front doors (≤30 chars), e.g. `Blackjack Pro - Khela World`, `Poker Pro - Khela World`,
  `Texas Holdem - Khela World`, `Roulette - Khela World`, `Slots - Khela World`.
  (Spell it "Blackjack", not "BlackJack".)
- **Apple Custom Product Pages** CANNOT change the app title — they only swap
  screenshots/preview/promo text and assign keywords. So on iOS the title stays
  `Khela World: Casino & Slots`, and you create one CPP per game (blackjack/poker/roulette/
  slots) with that game's screenshots + keyword, surfaced for those searches.
- **In-app deferred deep linking** — the listing/campaign a user came from sets which game is
  their default "home" on first launch. Reproduces "different main home per version" in one app.

> **DO NOT ship the same game as multiple near-identical apps** (one per game, differing only
> by the home screen). That is exactly Apple Guideline **4.3(b) "Spam"** (tightened June 2026)
> and Google's repetitive-content policy — penalty can be **termination of the whole Developer
> account**, not just one listing. The custom-listing approach above achieves the same outcome
> from one codebase, with consolidated (higher-ranking) ratings and zero ban risk.

---

## 9. Legal & compliance (before taking real money)

- Keep coins **non-cashable / non-convertible** → legal social casino, not gambling.
- **Never** let players win `$TOKEN` through play; token trades on exchanges only.
- App store rules: casino-themed games need age-gating + geo-restrictions; "Casino"/"Poker"
  in the title is fine for a *social* casino, but never imply real-money gambling.
- **Gambling law is jurisdiction-specific and criminal to get wrong** — gaming/gambling
  lawyer sign-off before launch.
- **Bangladesh:** crypto + gambling restricted — structure the token/company entity
  accordingly (likely offshore), keep it cleanly separate from the game, and use
  **geo-restrictions** on listings rather than crippling the global product.
- Token: fair launch, no profit promises, securities review.
- *This document is information, not legal or financial advice.*

---

## 10. Distribution & competitive strategy

You won't out-spend Zynga/Octro or the big social-casino studios. Win on a **wedge**:
1. **Region/language first** — Bengali / South-Asian audience + diaspora; in-language
   content, local channels. Own a small pond completely, then expand.
2. **Cross-promotion** — the `coinfolytics` crypto-content platform is owned distribution
   that funnels players (and later token attention) for near-zero marginal cost.
3. **Trust** — provably-fair games and (later) an on-chain, verifiable token model.

Avoid head-to-head with incumbents and avoid the Indian card-gaming trademarks (Adda52,
PokerBaazi, RummyCircle, Junglee) — don't name anything "Adda" or "Baazi."

---

## 11. Repo & workflow hygiene (fix soon)

- **Two trees are diverged.** All recent backend/Unity work (incl. `WalletService`) is in
  the `main` working tree and **uncommitted**; the Claude Code agent's worktree
  (`claude/reverent-davinci-7b2dc0`) is on an older commit **without it** and is flagged
  `prunable`. Pick one source of truth and reconcile (commit `main`, then rebase/merge the
  worktree) before the agent re-implements the ledger from scratch.
- **Build artifacts are tracked** (`bin/`, `obj/`, Unity `Library/`, `*.deps.json`). Fix
  `.gitignore` and untrack them.
- **Commit meaningful units** — nothing is committed; the work is at risk.
- Commit `CLAUDE.md` so every worktree inherits the rules.

---

## 12. Costs & honest risks

- Phase 0/1 cost is mostly your time + store fees (~$100) + assets. Token contracts only
  later, and a minimal standard ERC-20 + operational buy-and-burn avoids a big audit bill.
- **Most games get no traction** — fun/retention is hard; the token can't save a game
  nobody plays.
- Social casino is competitive and UA can be costly — the niche + owned distribution keep
  CAC sane.
- The token adds legal complexity — sequencing + dual-currency + a lawyer contain it.

---

## 13. Immediate next steps

1. Reconcile the git trees onto one branch; fix `.gitignore`; commit current work.
2. Wire `WalletService` into bet/settle (debit on deal, credit on settle, idempotent).
3. Build the IAP purchase flow + receipt validation → credit coins.
4. Finish Unity: Best SignalR client + card renderer + buy-coins + play loop.
5. Ship blackjack to a small Bengali/South-Asian audience; measure retention + whether
   strangers pay. That gate decides everything after it.
