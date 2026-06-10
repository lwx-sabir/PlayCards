# Project guide — Khela (social casino + revenue-backed token)

This file is read automatically by Claude Code. Follow it. It encodes hard-won
architecture decisions and the rules most likely to be violated by a well-meaning
refactor. When in doubt, prefer these rules over convenience.

## What we're building
A free-to-play **social casino** game (blackjack first; poker/roulette later) with
non-cashable in-game chips. Real money comes from in-app purchases. A **separate,
tradeable token** will later capture a slice of that revenue via buy-and-burn — it
is NOT the wager chip. The game must be fun and earn on its own; the token comes
much later. Sequencing: Phase 0 = fun + first paying players; Phase 1 = grow
revenue; Phase 2 = token. **Write no blockchain/token code until the game earns
real, growing revenue.**

## Architecture
- `khela/PlayCards/Khela.Game` — ASP.NET Core .NET 8 backend: JWT + Identity,
  MySQL (Pomelo EF Core), Redis, SignalR, REST API.
- `khela/PlayCards/Khela.Game/CradGames` — game-logic library (blackjack engine).
- `khela/PlayCards/Khela.Common` — DTOs shared between backend and client.
- `khela/PlayCards/PlayCard` — Unity client (URP, fully 3D). Namespaces `PlayCard.*`.

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

## Current state / next steps
- Done: blackjack engine, SignalR push, JWT/device auth, `WalletService` (ledger).
- Next: wire `WalletService` into bet/settle (record `WalletDebitTxId`/`WalletCreditTxId`
  on `GameHandParticipant`); build the IAP purchase flow + server-side receipt
  validation (Apple/Google) → credit Chips; Unity Best SignalR client + card renderer.
- Smaller fixes: natural blackjack should pay 3:2; remove the doubled
  `namespace CardGames.Blackjack` in `BlackjackGame.cs`.

## Definition of done for any change
1. `dotnet build` passes; no unexpected pending EF migration.
2. None of the NON-NEGOTIABLE rules above are weakened.
3. Money paths are idempotent and server-authoritative.
4. Work in small, reviewable steps; explain trade-offs; ask before large rewrites.
