# Three Card Poker — Unity client (slice #4)

A self-contained, plug-and-play 3CP game client that mirrors the blackjack Table-scene client exactly and
**reuses** the shared infrastructure (never duplicates it):

- **Shared, reused as-is:** `PlayCard.Account.AccountManager` (JWT/userId), `PlayCard.Core.AppConfig`
  (base URL), `PlayCard.App.GameSession` + `SceneNavigator` (scene handoff), `PlayCard.Game.Wallet.WalletManager`
  + the `BalanceHud`, and the card renderer `PlayCard.Game.Cards.*` (`CardVisual`, `CardSkin`, `CardId`,
  `CardPool`, `CardMover`). Also reuses `PlayCard.Game.Net.ApiResult<T>`.
- **3CP-owned (this folder), namespace `PlayCard.ThreeCardPoker.*`:**

| File | Role |
|---|---|
| `Dtos/TcpBoard.cs` | Client mirror of the server `ThreeCardPokerBoard` (masked snapshot) + `TcpCard.ToCardId()` |
| `Dtos/TcpRequests.cs` | Create/Join/PlaceBets request bodies + `TcpTableSummary` (lobby row) |
| `Net/ITcpHubClient.cs` | Live-channel interface (analogue of `IBlackjackHubClient`) |
| `Net/TcpRestClient.cs` | REST action channel `/api/threecard/*` + `GET /api/lobby/threecard` (singleton `Instance`) |
| `Net/TcpSignalRHubClient.cs` | Best SignalR transport → `/threecardhub` (production) |
| `Net/TcpPollingHubClient.cs` | REST-polling fallback (dev / IL2CPP-safe, no SignalR DLLs) |
| `Table/TcpTableController.cs` | Orchestrator: connect/join, one board path, intents (bets/deal/play/fold), heartbeat |
| `Table/TcpTableView.cs` | Diff-renders the board (fan + pool + mover); one hand/seat; dealer backs during acting |
| `UI/TcpActionBar.cs` | Play / Fold buttons (shown only while it's our turn to decide) |
| `UI/TcpBetPanel.cs` | Four bet circles (Ante + Pair Plus / Prime / 6-Card) + Deal |
| `UI/TcpResultBanner.cs` | Per-seat settle result (net across all circles; fold's side-bet win included) |

## Wiring the Table scene (editor)

Fastest path: **duplicate the blackjack Table scene** and swap the components (the layout, camera, felt, anchors,
card prefab and skin all carry over).

1. On the table root: add **`TcpTableController`**.
   - `Table View` → the `TcpTableView` (below).
   - `Hub Component` → drag a component implementing `ITcpHubClient`: **`TcpPollingHubClient`** for dev, or
     **`TcpSignalRHubClient`** for production (its `Hub Url` auto-derives `BaseApiUrl + /threecardhub` from
     `AppConfig` if you leave the localhost placeholder).
   - Standalone testing (no lobby): set `Debug Table Id` (from `GET /api/lobby/threecard`) and `Debug Seat`.
2. Add **`TcpTableView`**: assign the same `Card Prefab` + `Card Skin` blackjack uses, the `Dealer Anchor`, and the
   per-seat `Seat Anchors` (element 0 = seat 1). Reuse blackjack's fan/deal-animation settings.
3. **`TcpBetPanel`** (on an always-active object): wire the four amount labels + optional +/- steppers + `Deal`.
   Chips step by the table's per-circle min. (3D chip drag-and-drop onto four bet spots is a later polish.)
4. **`TcpActionBar`** (always-active object): wire `Play` + `Fold` buttons (+ optional countdown label).
5. **`TcpResultBanner`** (always-active object): wire the label. Point all three UI components' `Controller` at the
   `TcpTableController`.
6. Reuse the blackjack **`BalanceHud`** for the chips display — it listens to `WalletManager` and needs no 3CP code.

> UI visibility uses a `CanvasGroup` (not `SetActive`), so the watcher keeps its board subscription — this is the
> recurring "disabled watcher" trap. Keep `TcpBetPanel`/`TcpActionBar`/`TcpResultBanner` on always-active objects.

## Lobby → table handoff

`TcpRestClient.Instance.GetLobbyAsync()` returns the browsable `TcpTableSummary` list. To enter a table, set
`GameSession.TableId` and `GameSession.SeatNumber` (a **specific** seat — the masked 3CP board carries no user-id,
so `MySeat` is the seat you joined), then load the 3CP Table scene. Mirror the blackjack `LobbyTableCard.JoinSeat`
→ `SceneNavigator.GoToTable` pattern; if 3CP gets its own scene, add a scene constant beside `SceneNavigator.Table`
and branch on `GameSession.SelectedGame`.

## Not yet (deferred polish)

- 3D chip drag-and-drop onto four bet spots (currently a button/stepper bet panel).
- Per-seat moving camera + seat plates for other players (reuse blackjack's `TableCameraController`/`SeatPlates`).
- Dealer/seat avatars + BoZo dealer animations.
