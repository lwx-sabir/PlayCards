# Khela World — Master Project Plan

*Owner: Reza · Last updated: 2026-08-21*

This is the full plan and reasoning for the game project. The terse, must-follow
rules live in `/CLAUDE.md` (read automatically by Claude Code); this document is the
"why" behind them. When the two disagree, `CLAUDE.md` wins for code decisions.

---

## 1. Vision

A free-to-play **3D social casino**, **global from day one**. Players play casino games
(blackjack, three-card poker and video poker today; more to follow) using **non-cashable
in-game chips**, inside a light metagame of avatars, cosmetics and gifting. Real money
comes from **in-app purchases**. A **separate, revenue-backed token** comes much later.

Two things make this more than "another card app":
- **The live-ops + metagame stack** — pass, daily ladder, piggy bank, VIP, loyalty,
  missions, chests, avatars, cosmetics, gifting — is the retention + spend engine that
  pure card games lack, and it is the expensive-to-copy part of the product.
- **A licensable, certifiable engine.** The money path is built to real-money standards
  (idempotent, pessimistically locked, fully audited, provably fair) with a second
  trajectory in mind: **licensing the engine to gambling houses (B2B)**. Khela itself
  stays a social casino. This is why the wallet/ledger is deliberately over-built for an
  F2P title.

### What this is NOT (both of these were early AI-drafted scaffolding, now retired)
- **Not region-first.** An earlier draft called for a Bengali / South-Asian wedge.
  Retired: blackjack has no regional pull, and a small single-market cohort cannot answer
  the questions that matter. Khela is **global by design** — default English, and the
  locale set is never hardcoded (`Marketing:Locales`).
- **Not a single-game app.** *"Ship blackjack, then measure"* is retired too. Nobody
  installs a large single-game blackjack app when small free ones exist and none of it is
  real money. In this category the install decision is made on **catalog + live-ops**; the
  comparison set is Zynga Poker / Huuuge / Pokerist / Slotomania, not the small BJ apps.
  A Home picker offering one live table is a broken promise.

### Guiding principle
The game must be **fun and earn on its own**. The token is fuel bolted on *after* the
game makes real, growing revenue — never a substitute for a game people want to play.

### Constraints that shape every decision
- **Download size is a product metric**, not a late optimisation — it is the category's
  entry cost, and being materially smaller to install than the incumbents is a real
  acquisition advantage. (It has blown up once already: divergent URP quality tiers
  exploded the shader-variant superset, 453MB → 1.1GB.)
- **Finish rate, not scope, is the current constraint.** Many systems sit at 85–95%. An
  unfinished system cannot be charged for, shipped, or measured, so build velocity is
  currently converting at roughly half efficiency. Close a lane end to end before opening
  the next.

---

## 2. Sequencing (do not skip ahead)

The old *"Phase 0 = one game + IAP, then ship and measure"* is retired for the reasons in
§1. What replaces it is sequenced by **depth of finish**, not breadth of scope — because
the scope is largely already built and the shortfall is that too much of it stops at 90%.

**Stage A — Make it chargeable.** Close the four IAP integration pieces (store +
Apple/Google receipt validation → credit Chips; the chip-bundle shop screen; the piggy
break SKUs; the rewarded-ad SDK) and the sinks that give the currency meaning (the Kash
exchange, 1M chips = 1 Kash, currently unbuilt). The seams exist throughout the codebase
already — this is integration work against existing contracts, not design work.

**Stage B — Make the catalog credible.** Finish the clients for engines already built
(three-card poker, video poker) so Home offers a real list rather than a promise, and
close the cosmetics shop UI.

**Stage C — Launch globally and tune.** Store listings (§8), a size budget, localisation,
and a UA channel that costs less than it returns. Calibrate the live-ops economy against
real players rather than assumptions.

**Stage D — Token.** Only once revenue is real and growing (see §4). Fair launch,
transparent vesting, buy-and-burn from real IAP revenue.

> **Write no blockchain/token code until the game earns real, growing revenue.**

**Open question, deliberately not decided here:** the catalog today is *all card games*.
In this category slots is normally the revenue engine — highest ARPDAU and session count —
while card games carry retention. Video poker is the nearest thing already built and its
engine is pure and reusable. Whether slots is the next game is Reza's call.

---

## 3. Dual-currency model (the legal backbone)

This is the single most important design decision. Merging the two is illegal; keeping
them separate is what makes the whole thing work.

**In-game coins/chips — NOT a token, no cash-out.** Ordinary virtual currency in MySQL.
Bought with real money or earned through play/bonuses. Used to wager at tables and to
buy goods/apartments/gifts. **Cannot be withdrawn, traded, or converted to money or to
the token.** This is what keeps us a legal *social casino*, not real-money gambling.

**$TOKEN — the separate, tradeable, revenue-backed coin (Stage D).** A standard ERC-20
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
  flow) now; turn on NFT minting as an **optional later layer** once real demand is
  proven. The purchase flow is identical, so sequencing the chain part later costs nothing.
- Lawyer review before any on-chain sale; mind Bangladesh crypto restrictions (geo).

---

## 4. Token model (Stage D) — buy-and-burn

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
- `khela/Khela.Game/Khela.Web` — ASP.NET Core MVC **admin/ops dashboard** (separate app, shared
  `AppDbContext` + Redis; cookie-Identity admin gate). Ops tooling, not gameplay. See `docs/ADMIN_DASHBOARD.md`.

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
`GameHandActions`, and the provably-fair shuffle persist. **Leaderboards, profiles, and
social/gifts/chat/presence** are also built.

**Done (ops — admin dashboard, 2026-06-26):** `Khela.Web` — a dark-themed ASP.NET Core MVC admin console
(cookie-Identity admin gate, shared `AppDbContext` + Redis). Live modules: a real-data **Dashboard** (player /
economy / system-alert stats via read-only JSON APIs + a static loader), a **Settings** page whose Casino *and*
Game knobs are runtime-tunable (admin edits a Redis `khela:settings` hash that `ProgressionService` +
`BlackjackTableManager` overlay onto appsettings — live, no restart), and a read-only **Wallets & Ledger** audit
viewer over `WalletTransactions`. Remaining modules (Players, Reports, Leaderboards, Game Tables) are placeholders.
See `docs/ADMIN_DASHBOARD.md`.

**Done (leaderboards + per-game stats, 2026-06-27):** leaderboards **rebuilt to plain SQL** — all-time from
`UserGameStats` (per-game) / `UserProfile` (cross-game), windowed (daily/weekly/monthly) from a new `PlayerDailyStat`
daily rollup (upserted on settle, pruned 90d); `LeaderboardController` serves `/api/leaderboard?game&metric&period&scope&top`
+ `/boards` with self-rank (Global/Friends/Country). Overall board = **XP only** (farm-proof — never chip-balance/net-profit).
Profile endpoints now return **per-game stats** (`perGame[]`: per-game win-rate / biggest-win / streak / XP / last-played /
started-playing). The old Redis ZSET/seal/archive path is retired (dead write still fires — remove later). Migration
`AddPlayerDailyStats` applied; 44/44 tests green; endpoints smoke-verified vs real data. See `docs/LEADERBOARD.md`.

**Done (client):** the blackjack vertical slice is **assembled + playable** — Boot → Home
(config-driven carousel) → Lobby (table browser) → Table, with device-guest auth, REST action
channel + SignalR/polling transport, server-authoritative card rendering, action bar, result
banner, and balance HUD.

**Done since (2026-06-27 → 2026-08-21), in brief:** two more game engines — **three-card poker**
(deployed, live-money-smoked) and **video poker** (6 variants, `/verify`, tamper hash chain), both in
their own folders as plug-and-play modules. A complete live-ops stack: XP/levels, VIP, loyalty, daily
missions, admin chests, the monthly **pass**, the **daily-login ladder**, the **piggy bank** (accrual,
state machine, admin tiers) and the reward inbox — plus the shared **RewardFly** collect juice, avatars
and wardrobe (BoZo), the cosmetics catalog, Firebase social auth, public player IDs, Sonity audio, and
an expanded admin dashboard (Testing page, Piggy tab, config export→Redis seed). Wallet latency work cut
a reward claim from ~21 round trips to ~12.

**Next — the last mile, not new systems.** Each item below is one lane away from being chargeable or
shippable, and an unfinished lane returns nothing:

1. **Make it chargeable (Stage A).** Store + Apple/Google receipt validation → credit Chips; the
   chip-bundle shop screen (design exists in `docs/IAP_SHOP_DESIGN` notes, build does not); the piggy
   **break endpoint + three SKUs** (early / full / full×2 — payout ≠ banked on early, and `PiggyBreaks`
   records SKU, multiplier and both figures); the **rewarded-ad SDK** (daily catch-up days currently
   only work because the bypass switch is on). Item ids follow the SO/Mono id pattern from WGWB.
2. **Close the sinks.** The **Kash exchange** (1M chips = 1 Kash) is specified and unbuilt; the chip
   faucet is intentional, so the peg is what needs watching, not the faucet.
3. **Make the catalog credible (Stage B).** Three-card poker and video poker clients; cosmetics shop UI.
4. **Client polish.** Seat-pick (`int? SeatNumber` on `JoinTableRequest`), split-hand UI, bet validation,
   mobile-readable card faces; swap polling → Best SignalR for WebGL.
5. **Graphics tier selection in Settings** — a Low/Mid/High dropdown calling
   `PostFxTierController.SetTier()` (persists to `PlayerPrefs["khela.gfxTier"]`, which already wins over
   auto-detect). The post-processing half is built (`PP_Low`/`PP_Mid`/`PP_High` + controller). Remaining:
   the settings UI, and whether the tier should also drive `QualitySettings.SetQualityLevel`. ⚠️ Keep
   keyword settings consistent across tiers or the shader-variant superset explodes (§1).
6. **Size budget + global launch (Stage C).** World-scene lightmaps are currently too heavy for mobile.

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
  mapping directly onto the `GameDefinition`/`GameCatalog` system. **Now:** keep content
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

You won't out-spend Zynga / Playtika / Huuuge. But the earlier answer to that — *"win a
small Bengali / South-Asian pond first, then expand"* — is **retired** (§1): the audience
for a social casino isn't regional, blackjack has no regional pull, and a small cohort
can't validate a global product. The wedge is:

1. **Depth of live-ops for the size of the studio.** Pass, daily ladder, piggy, VIP,
   loyalty, missions, chests, social and gifting are already built. Most titles at this
   scale ship with two of those. It is the hard-to-copy surface, and it is done.
2. **Trust** — provably-fair games and a certifiable money path; the same work that makes
   the engine licensable B2B, so it pays twice.
3. **Size discipline** — materially smaller to install than the incumbents is a real
   acquisition advantage in this category, not a nicety.
4. **Cross-promotion** — the `coinfolytics` crypto-content platform is owned distribution
   that funnels players (and later token attention) for near-zero marginal cost.

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

- Stage A–C cost is mostly your time + store fees (~$100) + assets. Token contracts only
  later, and a minimal standard ERC-20 + operational buy-and-burn avoids a big audit bill.
- **Most games get no traction** — fun/retention is hard; the token can't save a game
  nobody plays.
- **The live risk is unfinished inventory, not scope.** A large amount of good work is
  sitting at 85–95% and returning nothing. Build rate is not the problem; conversion of
  build to shipped is.
- Social casino is competitive and UA can be costly — depth of live-ops, install size and
  owned distribution are what keep CAC sane, not a regional niche.
- **Download size** is both a UA cost and a retention cost in this category; treat a size
  regression as a bug.
- The token adds legal complexity — sequencing + dual-currency + a lawyer contain it.

---

## 13. Immediate next steps

*One rule for this list: **finish a lane before starting another.** An item at 90% returns nothing.*

1. ✅ Done — three game engines live; money path real-money-grade and DB-audited; the full live-ops
   stack (pass / daily / piggy / VIP / loyalty / missions / chests) built; admin dashboard + Testing
   page + config seed; blackjack client assembled and playable.
2. **Piggy break endpoint + its three SKUs**, then the store behind it. Highest-value dollar in the
   game, the server side is ~90% there, and closing it drags several other rows across the line.
3. **Store + Apple/Google receipt validation → credit Chips**, and the chip-bundle shop screen.
4. **Rewarded-ad SDK** — then turn `Rewards:BypassAdForMissedDays` **off**.
5. **Kash exchange** (1M chips = 1 Kash) — the sink that gives the chip faucet its meaning.
6. **Three-card poker + video poker clients**, so the Home picker offers a catalog rather than a promise.
7. Client polish (seat-pick, split UI, bet validation), graphics-tier settings UI, size budget.
8. Global launch + measure. Retention and conversion — not instinct — decide what gets built after this.
