# Khela World — Master Project Plan

*Owner: Reza · Last updated: 2026-06-19*

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

### Premium cosmetics & NFT policy

- **Paid-only, never won or gifted.** Premium cosmetics are *purchased* — never awarded by
  gameplay, RNG, loot boxes, or gifting. The casino only ever pays out non-cashable coins.
  This is what keeps us clear of gambling law and Google's "no paying for a chance to win
  NFTs" rule. **Non-negotiable.**
- **Apple-compliant flow:** pay via **In-App Purchase** (Apple's 15–30%, in fiat) → unlock the
  cosmetic → **mint the NFT server-side** as the on-chain record. The unlock is gated by the
  IAP, NOT by holding an externally-acquired NFT (the reverse is barred by Apple). No in-app
  links to external NFT marketplaces or crypto purchase on iOS.
- **Google:** allowed with transparency; do NOT market scarcity as appreciation/earning.
- **Custodial-by-default wallet**, with optional self-custody export for crypto-native players.
  Casual players never see gas or seed phrases; the minority who want true ownership/resale can
  export. This avoids the wallet-onboarding friction that kills casual conversion.
- **Scarcity:** total supply per item is **public** (live counter + serial numbers, e.g. #142/500).
  **Cosmetic-only — never gameplay-affecting** (limits securities exposure). No appreciation promises.
- **Sequencing:** ship cosmetics **off-chain first** (public counter + serials, same paid-only
  flow) in Phase 0/1; turn on NFT minting as an **optional later layer** once real demand is
  proven. The purchase flow is identical, so sequencing the chain part later costs nothing.
- Lawyer review before any on-chain sale; mind Bangladesh crypto restrictions (geo).

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

- `khela/Khela.Game/Khela.Game` — ASP.NET Core .NET 8 backend: JWT + Identity, MySQL
  (Pomelo EF Core), Redis, SignalR, REST API, Serilog.
- `khela/Khela.Game/CradGames` — game-logic library (blackjack engine).
- `khela/Khela.Common` — DTOs shared between backend and Unity client.
- `khela/Khela.Play` — Unity client (URP, fully 3D). Namespaces `PlayCard.*`; assets under
  `Assets/1Khela`. *(Layout flattened ~2026-06; the old `khela/PlayCards/*` nesting is gone.)*

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

*Updated 2026-06-19. The money path was DB-audited sound — see `docs/DB_AUDIT_2026-06-19.md`.*

**Done (server, live + DB-audited):** blackjack engine (hit/stand/double/split/insurance, dealer
logic, **3:2 naturals**, casino-standard split — any two 10-value cards split, split aces get one
card), SignalR push, JWT + device auth, Redis table state, hand-history audit. The **wallet is
wired end-to-end**: `WalletService` (idempotent debit/credit, `SELECT…FOR UPDATE`, wagerable guard,
signed-delta, `RowVersion` as `DateTime?`) now drives **debit-on-bet + credit-gross-on-settle**;
players are **seated from their real wallet**; a **`BlackjackRoundDriver`** (2s tick) auto-stands
expired turns + auto-settles. Per-hand settle audit (`GameHandParticipant.HandIndex`), move-by-move
`GameHandActions`, and the provably-fair shuffle persist. Phase-1 **leaderboards, profiles, and
social/gifts/chat/presence** are also built.

**Done (client):** the blackjack vertical slice is **assembled + playable** — Boot → Home
(config-driven carousel) → Lobby (table browser) → Table, with device-guest auth, REST action
channel + SignalR/polling transport, server-authoritative card rendering, action bar, result
banner, and balance HUD.

**Next (Phase 0 → revenue), in order:**
1. **IAP purchase flow** — `StoreController` + server-side Apple/Google receipt validation →
   credit Chips via `WalletTransaction`. This is the literal "take money" step and the remaining
   Phase-0 gate.
2. **Client gameplay polish** — seat-pick (clickable seats + join-by-seat: add `int? SeatNumber`
   to `JoinTableRequest`), split-hand UI, bet validation, card dealing animations, dealer + avatar
   models, mobile-readable card faces; swap polling → Best SignalR for WebGL.
3. **Ship blackjack** to a small Bengali/South-Asian audience; measure retention + whether
   strangers pay. That gate decides everything after it.

**Smaller fixes:** remove the doubled `namespace CardGames.Blackjack` in `BlackjackGame.cs`; wire
`GameHandSnapshot` persistence (deal/settle board JSON+hash — schema exists, unwired); add a
`PrevHandHash` chain for tamper-evident round linking.

---

## 7. Unity client setup (URP, 3D)

- **Pipeline:** URP. After importing any Built-in-pipeline assets, run the **Render
  Pipeline Converter** (materials show magenta until converted).
- **UI stack — ONE visual language.** Use **GUI PRO – Casual Game** (Layer Lab, owned) as
  the *single* UI framework for ALL screens: home, shop, coin HUD, popups, settings, and the
  social/apartment/gifting screens. Its bright/glossy/rounded style is the right fit for a
  social casino. Do **NOT** mix in GUI PRO *Survival Clean* or *Fantasy RPG* (different
  themes → patchwork, amateur look), and do **NOT** add ricimi (a redundant second framework).
  Skin Casual with a **casino icon/art pack** (chips, cards, coins, gold accents) applied
  consistently to give it casino identity. Borrow an element from another kit only if
  re-skinned to match Casual. UI is screen-space Canvas (uGUI) — pipeline-agnostic, works in
  URP as-is. **Presentation only:** coin balances, shop prices, and purchases come from the
  server (wallet + IAP), never from demo/kit logic.
- **SignalR:** Best SignalR (Best HTTP + WebSockets + SignalR bundle). Implement
  `BlackjackHubClient` against `IBlackjackHubClient`; JWT via `?access_token=`.
- **Card pack (AarniTuli, itch.io ZIP):** it's a `.blend`-based UPM package (`package.json`
  + asmdef). Install requires **Blender installed**, and must preserve the original
  `.meta`/GUIDs (the broken import lost guid `47aaf…` for `CardBase`). Install via Package
  Manager "Add package from disk" → its `package.json`, or drop the inner package folder
  into `Packages/`. **Use it as a renderer only** — never its local deck/shuffle. Map the
  server's `FaceValue`/`Suit` enums to the pack's `value`/`Suit` (it has `suitToInt()`);
  ideally promote those enums to `Khela.Common` so both sides share one definition.
- **Asset delivery — Addressables, grouped per game/feature.** Structure all heavy assets
  (3D models, textures, avatar/wardrobe content, table skins, each game's art) as **Unity
  Addressables** groups — one per game/feature — so the app ships small and content streams at
  runtime. A **boot/loader scene** downloads required content with a progress bar; optional
  content (other games, cosmetics, avatar packs) loads **on demand** when the player opens it,
  mapping directly onto the `GameDefinition`/`GameCatalog` system. **Phase 0:** keep content
  **local / in-build** (no CDN yet). **Before launch:** flip the same groups to **remote
  delivery** via a CDN (Unity CCD / S3+CloudFront / Cloudflare R2), plus **Google Play Asset
  Delivery** on Android (to clear the ~200 MB base-AAB limit) and a CDN (or On-Demand
  Resources) on iOS. The remote catalog lets you push new *assets* without an app-store update
  (assets/data only — never executable code). **Group as Addressables now even while local** —
  retrofitting an asset-heavy project later is painful. Budget for CDN bandwidth at scale.

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

## 11. Repo & workflow hygiene

- **Resolved (2026-06-19):** backend + Unity work is **committed on `main`** in meaningful units
  (recent: `table`, `home`, `social-chat`, the unity client, leaderboards/social schema). Build
  artifacts (`bin/`, `obj/`, Unity `Library/`) are **gitignored** (0 tracked). The old
  `khela/PlayCards/*` nesting has been flattened to `khela/Khela.Game` + `khela/Khela.Play` +
  `khela/Khela.Common`.
- **Still loose:** the root `CLAUDE.md`/`AGENTS.md` are now synced to current; the agent-worktree
  copy of `CLAUDE.md` is untracked — commit it on `main` so every worktree inherits the rules.
  Agent sessions run in throwaway worktrees, but the buildable code is the **main checkout** — do
  backend/Unity file work there, with absolute `D:\Projects\PlayCards\khela\...` paths.

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

1. ✅ Done — git trees reconciled + `.gitignore` fixed + work committed; `WalletService` wired into
   bet/settle (debit-on-bet + credit-on-settle, idempotent); the blackjack vertical slice is playable.
2. **Build the IAP purchase flow + Apple/Google receipt validation → credit Chips** — the remaining
   Phase-0 gate (the "take money" step).
3. Client gameplay polish: seat-pick, split UI, bet validation, dealing animations, dealer/avatars;
   swap polling → Best SignalR for WebGL.
4. Ship blackjack to a small Bengali/South-Asian audience; measure retention + whether strangers
   pay. That gate decides everything after it.
