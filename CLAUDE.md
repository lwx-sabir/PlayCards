# Project guide — Khela (social casino + revenue-backed token)

This file is read automatically by Claude Code. Follow it. It encodes hard-won
architecture decisions and the rules most likely to be violated by a well-meaning
refactor. When in doubt, prefer these rules over convenience.

## What we're building
A free-to-play **social casino** with non-cashable in-game chips, built **global from day
one**. Real money comes from in-app purchases. A **separate, tradeable token** may later
capture a slice of that revenue via buy-and-burn — it is NOT the wager chip, and **no
blockchain/token code gets written until the game earns real, growing revenue.**

**The product is the catalog + the live-ops, not any one game.** In this category the
install decision is made on the game list and the retention systems, not on the card
engine — the comparison set is Zynga Poker / Huuuge / Pokerist / Slotomania, not small
single-game blackjack apps. A Home that offers one live table is a broken promise, and
*"ship blackjack alone and measure"* is **retired** as a plan: nobody installs a large
single-game blackjack app when small free ones exist and none of it is real money.
Breadth of catalog and depth of live-ops (pass, daily, piggy, VIP, loyalty, missions,
chests, social) are the competitive surface.

**Global, not region-first.** Blackjack has no regional pull, and a small single-market
cohort can't answer the questions that matter. Default English; never hardcode a locale set.

**Download size is a product metric** — the category's entry cost. Watch it per build, not
as a late optimisation.

**Second trajectory:** the engine is intended to be **licensed to gambling houses (B2B)**,
so money paths are built to be *certifiable*. Khela itself stays a social casino. That is
why the wallet/ledger is deliberately over-built for an F2P title — it is not gold-plating.

**The current constraint is finish rate, not scope.** Many systems sit at 85–95%, and an
unfinished system can't be charged for, shipped, or measured. Close a lane end to end
before opening the next.

Full strategy + the "why": `docs/PROJECT_PLAN.md`.

## Architecture
- `khela/Khela.Game` — ASP.NET Core .NET 8 backend: JWT + Identity,
  MySQL (Pomelo EF Core), Redis, SignalR, REST API.
- `khela/Khela.Game/CradGames` — game-logic library (blackjack engine).
- `khela/Khela.Common` — DTOs shared between backend and client.
- `khela/Khela.Play` — Unity client (URP, fully 3D). Namespaces `PlayCard.*`.
  (Layout flattened ~2026-06; the old `khela/PlayCards/*` nesting is gone.)

## NON-NEGOTIABLE RULES (do not break these)

1. **Server-authoritative gameplay.** The server deals, shuffles, and settles. The
   client only *renders* board snapshots and *sends actions*. The client must NEVER
   generate cards, decide outcomes, or hold an authoritative balance. Specifically:
   do NOT use the card asset pack's local deck (`CardsAssetPackDeck.DrawAndCreateCard`
   / `Shuffle`); use it as a *renderer* only — build the exact card the server sent
   via `new CardAssetPackCard(value, suit)` + `SetCardValues` + `updateGraphics`.

2. **Dual-currency legal guardrail.** In-game `Chips`/`Coins` are non-cashable play
   money (this is what keeps us a legal social casino, not real-money gambling). The
   token currency must NEVER be bet or won at a table. Only `Chips`/`Coins` are
   wagerable — this is enforced in code at the wallet/engine boundary
   (`WalletService`). Never relax it.

3. **Wallet integrity.** All balance changes go through `IWalletService`
   (debit-on-bet, credit-on-settle), which is idempotent on `CorrelationId` and uses
   pessimistic `SELECT ... FOR UPDATE` locking. Chips live in MySQL, never on-chain.
   Never trust a client-supplied balance — seat players from their real wallet.
   `WalletTransaction.Amount` is a signed delta (BalanceBefore + Amount == BalanceAfter).

4. **No real-money gambling, no custody.** Players never win the token by playing.
   When the token ships it is non-custodial and revenue-backed (buy-and-burn from
   real IAP revenue, never funded by selling tokens). A lawyer reviews before launch.

## Networking
- **Actions** (bet/hit/stand/deal/double/split) go over **REST** (`BlackjackController`).
- **Live state** is pushed over the **SignalR hub** (`BlackjackHub` → `TableUpdated`
  board snapshots). The Unity client subscribes via `IBlackjackHubClient`.
- Unity transport is **Best SignalR** (chosen for mobile/WebGL reliability). Keep all
  transport behind `IBlackjackHubClient` so it can be swapped without touching game code.
  (A `PollingBlackjackHubClient` fallback exists and is fully playable for turn-based blackjack.)
- WebGL note: browsers can't send an auth header on the WS handshake, so the token
  goes as `?access_token=`. The server's JwtBearer needs an `OnMessageReceived` hook
  to read it from the query string for the `/blackjackhub` path.

## Conventions (match existing style)
- Backend namespaces `Khela.Game.*`; client `PlayCard.*`.
- EF money is `decimal(18,4)`. Use idempotency keys, `RowVersion` concurrency,
  `BalanceBefore/After` audit. Interface + DI registration. XML doc comments.
- Do NOT change DB schema/table/column names casually; add migrations deliberately.
- `PlayerWallet.RowVersion` is `DateTime?` (MySQL `timestamp(6)` rowversion) — never
  revert it to `byte[]` (unsupported on MySQL).
- Distinguish the brand from the cryptocurrency: never blind-replace "bitcoin".
- Client assets live under `Assets/1Khela` (scenes in `_Scenes`, game configs in `Game/Definitions`).
  Editor **Create** menus are under `Khela/` (e.g. `Khela ▸ Game Definition`) — not `PlayCard/`.

## Client — Home & multi-game
- **Home is a game *picker* only** — no table/bet info there (that lives in the Lobby). It's a circular
  `CarouselController` (`PlayCard.Home`) of 3D tables; each `GameTable` references a `GameDefinition`.
- **Games are config-driven.** `GameDefinition` ScriptableObjects (key / displayName / category /
  `available` / branding) + a `GameCatalog` registry, all under `Assets/1Khela/Game/Definitions`. Adding a
  game is a config asset, not code. Only `available` games route; coming-soon games auto-disable Play Now /
  Lobby. Blackjack, **three-card poker** and **video poker** have engines (the latter two have scaffolded,
  unfinished clients); the rest (poker, holdem, teenpatti, callbreak, roulette, slots, craps, bingo, sports)
  are coming-soon placeholders.
- **Flow:** Home → (**Play Now** = server auto-match by level + open seat | **Lobby** = table browser) →
  Table. The chosen game is carried in `GameSession.SelectedGame`; the Lobby + auto-match filter by it.
- **Device-guest auth:** email + password are BOTH derived deterministically from
  `SystemInfo.deviceUniqueIdentifier` (`AuthHelpers`) so a device always re-derives the same login (the
  local save is a cache, not the source of truth). Never make guest creds random again — a pre-existing
  account then collides ("Email already exists") and the device is permanently locked out.

## Current state / next steps
*Updated 2026-08-21. Full status: `docs/PROJECT_PLAN.md` §6; money audit: `docs/DB_AUDIT_2026-06-19.md`.*
- **Server, live:** three engines — **blackjack** (hit/stand/double/split/insurance, 3:2 naturals,
  casino-standard split, dealer peek, provably-fair shoe + cut card), **three-card poker** (deployed,
  live-money-smoked), **video poker** (6 variants, `/verify`, hash chain). Money path is real-money-grade:
  `WalletService` debit-on-bet + credit-gross-on-settle, idempotent on `CorrelationId`, `SELECT … FOR UPDATE`,
  signed-delta audit, gifted/clean split; ~500 tests. Live-ops built end to end: XP/levels, VIP, loyalty,
  daily missions, chests, monthly **pass**, **daily-login ladder**, **piggy bank** (accrual + state + admin),
  reward inbox, leaderboards, profiles, social/gifts/chat/presence. Auth: deterministic device-guest +
  Firebase social. Admin dashboard (`Khela.Web`) incl. a Testing page and a config export→Redis seed path.
- **Client:** blackjack assembled + playable (Boot → Home → Lobby → Table) with dealer deal animation,
  per-seat cameras, card skins, betting/chips, Sonity audio, avatars/wardrobe, post-FX tiers. Pass / daily /
  piggy screens built on the shared **RewardFly** collect juice.
- **The backlog is the last mile, not new systems.** Each of these is one lane from being chargeable or
  shippable: three-card poker client, video poker client, piggy **break endpoint + SKUs**, cosmetics
  **shop UI**, rewarded-ad **SDK**, the **Kash sink** (1M chips = 1 Kash exchange, unbuilt), VIP IAP spend,
  world-scene lightmaps (too heavy for mobile), Invector controller (live prefab won't fire).
- **IAP is integration, not a gate.** The seams exist already (`PlayerPass.Paid`, `PiggyConfig.PriceSku`,
  dormant VIP spend, `PiggyPanel.BreakRequested`). Four pieces: store + Apple/Google receipt validation →
  credit Chips; the chip-bundle shop screen; the piggy break SKUs; the rewarded-ad SDK. Item ids follow the
  SO/Mono id pattern already used in the WGWB project.
- **Smaller fixes:** remove the doubled `namespace CardGames.Blackjack` in `BlackjackGame.cs`; wire
  `GameHandSnapshot` persistence (schema exists, unwired); `PrevHandHash` round-chaining is unused.

## Definition of done for any change
1. `dotnet build` passes; no unexpected pending EF migration.
2. None of the NON-NEGOTIABLE rules above are weakened.
3. Money paths are idempotent and server-authoritative.
4. Work in small, reviewable steps; explain trade-offs; ask before large rewrites.
