# Project guide — Khela (social casino + revenue-backed token)

This file is read automatically by Codex. Follow it. It encodes hard-won
architecture decisions and the rules most likely to be violated by a well-meaning
refactor. When in doubt, prefer these rules over convenience. (Kept in sync with `CLAUDE.md`.)

## What we're building
A free-to-play **social casino** game (blackjack first; poker/roulette later) with
non-cashable in-game chips. Real money comes from in-app purchases. A **separate,
tradeable token** will later capture a slice of that revenue via buy-and-burn — it
is NOT the wager chip. The game must be fun and earn on its own; the token comes
much later. Sequencing: Phase 0 = fun + first paying players; Phase 1 = grow
revenue; Phase 2 = token. **Write no blockchain/token code until the game earns
real, growing revenue.** Full strategy + the "why": `docs/PROJECT_PLAN.md`.

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
  Lobby. Blackjack is the only built game; the rest (poker, holdem, teenpatti, callbreak, roulette, slots,
  craps, bingo, sports) are coming-soon placeholders.
- **Flow:** Home → (**Play Now** = server auto-match by level + open seat | **Lobby** = table browser) →
  Table. The chosen game is carried in `GameSession.SelectedGame`; the Lobby + auto-match filter by it.
- **Device-guest auth:** email + password are BOTH derived deterministically from
  `SystemInfo.deviceUniqueIdentifier` (`AuthHelpers`) so a device always re-derives the same login (the
  local save is a cache, not the source of truth). Never make guest creds random again — a pre-existing
  account then collides ("Email already exists") and the device is permanently locked out.

## Current state / next steps
*Updated 2026-06-19. Full status: `docs/PROJECT_PLAN.md` §6; money audit: `docs/DB_AUDIT_2026-06-19.md`.*
- **Done (server, live + DB-audited):** blackjack engine (hit/stand/double/split/insurance, dealer logic,
  **3:2 naturals**, casino-standard split — any two 10-value cards split, split aces get one card). SignalR
  push; JWT/device auth; Redis table state. **Wallet wired end-to-end:** `WalletService` drives
  **debit-on-bet + credit-gross-on-settle** (idempotent on `CorrelationId`, `SELECT … FOR UPDATE`); players
  are seated from their real wallet; a **`BlackjackRoundDriver`** (2s tick) auto-stands expired turns +
  auto-settles. Per-hand settle audit (`GameHandParticipant.HandIndex`), move-by-move `GameHandActions`, and
  the provably-fair shuffle all persist. Phase-1 **leaderboards / profiles / social / gifts / chat / presence**
  also built.
- **Done (client):** blackjack vertical slice **assembled + playable** — Boot → Home (config-driven
  carousel) → Lobby (table browser) → Table, with device-guest auth, REST action channel + SignalR/polling
  transport, server-authoritative card rendering, action bar, result banner, and balance HUD.
- **Next (Phase-0 gate):** **IAP purchase flow + Apple/Google receipt validation → credit Chips** (the
  "take money" step). Then client polish: **seat-pick** (add `int? SeatNumber` to `JoinTableRequest`),
  split-hand UI, bet validation, dealing animations, dealer/avatar models, mobile-readable cards, swap
  polling → Best SignalR. Then ship to a small Bengali/South-Asian audience and measure retention + whether
  strangers pay.
- **Smaller fixes:** remove the doubled `namespace CardGames.Blackjack` in `BlackjackGame.cs`; wire
  `GameHandSnapshot` persistence (schema exists, unwired); `PrevHandHash` round-chaining is unused.

## Definition of done for any change
1. `dotnet build` passes; no unexpected pending EF migration.
2. None of the NON-NEGOTIABLE rules above are weakened.
3. Money paths are idempotent and server-authoritative.
4. Work in small, reviewable steps; explain trade-offs; ask before large rewrites.
