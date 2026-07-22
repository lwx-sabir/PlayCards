# Networking Spec — Khela

*How Khela's real-time and turn-based multiplayer are wired. Target: `khela/Khela.Game`
(ASP.NET .NET 8 backend), `khela/Khela.Play` (Unity URP client), and a new **Unity headless
world server**. Honors all CLAUDE.md NON-NEGOTIABLES — above all: **money is authoritative in
ASP.NET only, never in the Unity netcode.***

---

## 0. The governing principle: two planes, one money authority

Khela has **two independent networking planes**, chosen because the games have two fundamentally
different shapes. They must never be merged, and only one of them is ever allowed to touch money.

| Plane | Games | Transport | Authority | Money? |
|---|---|---|---|---|
| **A — Turn-based tables** | Blackjack, Teen Patti, 3 Card Poker, Video Poker, Hold'em | REST + **SignalR** to ASP.NET | **ASP.NET** (server-authoritative) | **YES — the money plane** |
| **B — Real-time world** | Walkable social world, hunting tournament | **FishNet** (Unity headless server) | Unity world server (gameplay only) | **NO — reports to ASP.NET; never mints** |

**The one rule that ties them together:** the **FishNet world server NEVER creates, grants, or
mutates Chips/Kash.** All money — hunting payouts, item purchases, anything of value — flows through
the ASP.NET **`WalletService`** (idempotent, `SELECT … FOR UPDATE`, dual-currency guarded) via
authenticated server-to-server calls. The world server **orchestrates gameplay and reports
outcomes**; ASP.NET **decides and grants money.** This keeps the entire money path inside the one
place that's already audited, idempotent, and legally firewalled.

---

## 1. Why FishNet, not Photon Quantum/Fusion

- **Quantum** is a deterministic ECS lockstep engine for **frame-precise competitive combat**
  (fighting games, RTS). Khela has none of that: the tables are turn-based (ASP.NET), and the
  real-time content (walking around, hunting animals) is casual/score-based, arbitrated by a server
  timestamp — **latency-tolerant, not frame-deterministic.** Quantum would force an ECS rewrite,
  fight the GameObject asset stack (Invector, BoZo, Malbers, Animancer), cost per-CCU, and solve a
  determinism problem Khela doesn't have.
- **Fusion** is the apples-to-apples Photon product (GameObject state-sync) but is a paid/hosted
  alternative to **FishNet**, which is **free, open-source, GameObject-based, transport-agnostic**,
  ships **interest management + client prediction + reconnect**, and already covers **mobile/PC/web**.
- **Decision: FishNet** for Plane B. Locked.

---

## 2. Plane A — Turn-based tables (existing, ASP.NET-authoritative)

Do **not** move the card games onto FishNet. They are turn-based and already correctly
server-authoritative in ASP.NET; FishNet buys them nothing and would move money off the audited path.

- **Actions** (bet/hit/stand/deal/raise/fold) → **REST** (`BlackjackController` etc.).
- **Live board state** → **SignalR** push (`BlackjackHub` → `TableUpdated` snapshots), behind the
  `IBlackjackHubClient` abstraction so transport can swap without touching game code.
- **Transport:** **Best SignalR (Bundle)** on the Unity client (mobile/WebGL reliability); a
  `PollingBlackjackHubClient` fallback is fully playable for turn-based tables.
- **WebGL note:** browsers can't send an auth header on the WS handshake — the JWT goes as
  `?access_token=`; the server's JwtBearer `OnMessageReceived` reads it for the `/blackjackhub` path.
- **Money:** every wager/settle goes through `WalletService` (idempotent on `CorrelationId`). Already
  built and audited. Pot games (Teen Patti/Hold'em) use the pot-escrow ledger pattern (see their specs).
- **Anti-collusion (PvP tables):** device-correlation at **seat time** (reuse the guest device id) —
  block two correlated devices from the same table before seating. See TEEN_PATTI_SPEC / TEXAS_HOLDEM_SPEC.

Plane A stays exactly as designed. This spec's new work is all Plane B.

---

## 3. Plane B — the Unity headless world server (FishNet)

The walkable world and hunting run on a **dedicated Unity headless server** — a **separate process**
from ASP.NET, containerized and room-instanced.

- **This is a second server stack.** You now run ASP.NET (auth, wallet, tables) **and** a Unity
  headless world server. Budget the ops: containerized instances, **scale-to-zero when a room is
  empty**, a matchmaker that assigns players to instances.
- **Transports (FishNet Multipass — run more than one at once):**
  - **Tugboat (UDP)** — mobile + PC.
  - **Bayou (WebSocket)** — WebGL/web (browsers can't do raw UDP).
  - One server, both transports, same simulation.
- **Rooms/instances:** each social space (pub, club, apartments) and each hunting land is a
  **server-authoritative instance** with a **player cap** (~10–30; lower on mobile). Bigger demand →
  **shard into multiple instances** of the same space.
- **Interest management (mandatory):** only sync entities near each player. Essential for a crowded
  club and for shared-animal hunting; it's the difference between "works" and "melts mid-tier phones."

### 3.1 Cross-plane auth
- ASP.NET issues the **JWT** (device-guest identity, deterministic from `deviceUniqueIdentifier`).
- Before seating a player, the **world server validates that JWT server-to-server against ASP.NET**
  (or verifies the signature with the shared key). The player's identity + entitlements carry into
  the world; the world server never invents identity.
- The same identity ties world actions back to the correct ASP.NET wallet for payouts.

---

## 4. The social walkable world (casual, low-stakes)

- **What syncs:** avatar **transform + animation state**, presence, emotes, chat routing. That's it.
- **Authority:** server-authoritative but **low-stakes** — nothing here touches money, so the worst
  case of a movement exploit is a cosmetic teleport. Use server-authoritative movement with client
  prediction + interpolation for smoothness; don't over-engineer anti-cheat for a lounge.
- **Sit/interact:** furniture stays **static** (see the chairs discussion) — "sitting" is a
  **networked state** (`seatId → occupantId`) + a locally-played animation, **never** a synced
  transform. Replicate events/state, not furniture transforms.
- **Latency tolerance: high.** It's a social space; interpolation hides jitter. Polling-grade
  responsiveness is fine.

---

## 5. Hunting tournament (server-authoritative, money-relevant, SHARED animals)

The one real-time mode where **chips and rank are on the line**, so it's fully server-authoritative.

### 5.1 The server owns the animals
- Each animal's **HP, position, AI, alive/dead, and per-player damage** live on the **world server**.
- **Malbers AI runs server-side** (Malbers is single-player by default — net-sync it like Invector):
  authoritative logic on the server, transforms/state replicated to clients, **clients render only**.
- **Interest management** on animals — sync only those near each hunter.

### 5.2 Server-side hit validation (anti-aimbot — chips at stake)
- Client sends **"I fired at this angle/point"** (input), never "I killed animal X".
- **Server raycasts against its own animal positions**, applies damage, decides death + credit.
- Plausibility checks: **fire rate**, aim sanity, bullet budget. Combined with the **spawn cap** (the
  payout ceiling) and the **5-min timer**, this bounds what any client can extract.

### 5.3 Shared-animal kill credit (the key design rule)
Animals take 1–3 bullets and multiple hunters will hit the same one. **Default: damage-share** —
chips split proportional to damage dealt. This removes the **kill-steal rage** of last-hit and needs
no contention arbitration beyond tracking per-player damage. Make the rule **config** (damage-share /
last-hit / tag-window); ship **damage-share**.

### 5.4 Latency fairness
- Low-ping hunters win the scramble — inherent to shared resources. **Start with
  server-receive-order, no lag compensation.** It's a casual chip hunt, not CS; ping mattering a bit
  is acceptable. Add favor-the-shooter lag comp **later** only if playtesting demands it. **Never**
  make hits client-authoritative.

### 5.5 Economy with a field of hunters
- **Spawn rate scales with player count** (N hunters can't starve on a solo rate) but **sublinearly**,
  so each player's *expected* chip share stays **≤ their bought-bullets-worth** — house margin holds,
  and the contention itself is the variance/gamble.
- **Entry:** buy bullets with **Kash** (1 Kash = 20 bullets, capped near the useful ceiling).
- **Payout:** at tournament end the **world server reports final per-player scores to ASP.NET** →
  leaderboard + **chip payout through `WalletService`** (idempotent). The world server never grants
  chips itself.
- Hunting rewards are **Chips, non-cashable, never the token**; keep hunting on its **own progression
  track** (don't feed the wagering XP/leaderboards — closes a farm path).

### 5.6 Scale
- Player cap per land instance (~10–30); big tournaments = **many parallel land instances feeding one
  leaderboard** (the video-poker-tournament pattern: parallel sessions, one ranking).

---

## 6. The money boundary (NON-NEGOTIABLE)

- The **FishNet world server never mints, grants, or mutates Chips/Kash.** Full stop.
- All money effects (hunting payout, Kash item purchase, Chips↔Kash exchange) are **authenticated
  server-to-server calls from the world server to ASP.NET `WalletService`**, **idempotent** on a
  composite key (e.g. `hunt:{tournamentId}:{userId}:payout`), so a retry or a world-server crash can
  never double-pay.
- **Conservation/audit** lives in ASP.NET, same as the tables. The world server holds *gameplay*
  truth (who shot what); ASP.NET holds *money* truth.
- **Dual-currency guard intact:** only Chips are wagerable; Kash is non-wagerable; the token is never
  touched by any plane.

---

## 7. Anti-cheat summary (by plane)
- **Tables (Plane A):** server-authoritative outcomes (already built); PvP collusion → device
  correlation at seat time + win/loss graph batch job (Teen Patti/Hold'em specs).
- **World (Plane B, social):** low-stakes; server-authoritative movement, minimal.
- **Hunting (Plane B, money):** server-side hit validation + fire-rate/plausibility + spawn cap +
  server-owned animals. No client-authoritative kills. Bots (for liquidity/seeding) are **tagged**
  so they're excluded from collusion signals, progression, and payouts.
- **Universal:** device-guest identity (`deviceUniqueIdentifier`) is the shared anti-abuse primitive
  across both planes.

---

## 8. Reconnect / timeout
- **Tables:** existing heartbeat + stale-seat reaper (server reaps a disconnected seat ~30s after
  heartbeat stops; never pulls a live in-round stake) — see the money-path audit. Client must send
  the heartbeat + a `LeaveTable` on scene exit (the known APK bug was the client not doing this).
- **World/hunting (FishNet):** reconnect grace window; on reconnect the client **rehydrates from
  server-authoritative state** (never client-reported). A hunter who drops mid-tournament keeps their
  server-tracked score; prolonged disconnect → removed from the instance, final score stands.

---

## 9. Cross-platform (mobile-first, also PC/web)
- **Mobile/PC:** Tugboat (UDP). **Web (WebGL):** Bayou (WebSocket) — no raw UDP in browsers; expect
  higher latency, keep the world casual there.
- **Player caps are per-platform:** interest management fixes *network* cost, but rendering 30 BoZo
  avatars is a *client* cost — cap mobile instances lower than PC.
- The tables' SignalR/polling already works across all three (with the `?access_token=` WebGL hook).

---

## 10. Reuse vs new
**Reused (built):** ASP.NET auth/JWT/device-guest; `WalletService` (idempotent money); the table
SignalR/REST stack + `IBlackjackHubClient` + Best SignalR + polling fallback; the stale-seat reaper;
device-correlation anti-abuse primitive.

**Genuinely new:**
1. A **Unity headless world server** (FishNet, Multipass Tugboat+Bayou), containerized + room-instanced
   + scale-to-zero + a matchmaker.
2. **Cross-plane auth** (world server validates ASP.NET JWTs).
3. **Networked, server-authoritative Malbers animals** (server AI + replicated state) for hunting.
4. **Server-side hit validation + damage-share kill credit + field-scaled spawn economy.**
5. **World→ASP.NET server-to-server money bridge** (idempotent payout/purchase calls).
6. Server-authoritative avatar presence + networked seat/interact state for the social world.

---

## 11. Definition of done
- Two planes cleanly separated; tables stay on ASP.NET (SignalR/REST), world/hunting on FishNet.
- **No money path in the FishNet server** — every chip/Kash effect is an idempotent server-to-server
  call into `WalletService`; conservation/audit unchanged; dual-currency + token firewall intact.
- World server: FishNet Multipass (Tugboat + Bayou), room-instanced, per-platform player caps,
  interest management, scale-to-zero, JWT-validated seating.
- Hunting: server owns animals (Malbers server-side), server-validates every hit, damage-share
  credit, spawn scales sublinearly with field size, final scores reported to ASP.NET for
  leaderboard + payout; hunting on its own progression track.
- Reconnect rehydrates from server state (both planes); client sends table heartbeat + LeaveTable.
- Cross-platform (mobile/PC/web) verified; `dotnet build` + Unity build green; no NON-NEGOTIABLE weakened.

---

## Non-negotiables recap
1. **Money is authoritative in ASP.NET only.** The FishNet world server never mints/grants/mutates
   currency — it reports outcomes; ASP.NET grants via idempotent `WalletService`.
2. **Tables stay on ASP.NET** (turn-based, server-authoritative); do not move them to FishNet.
3. **Server-authoritative everywhere money or rank is at stake** (tables, hunting); clients render +
   send inputs, never decide outcomes or hold authoritative balances.
4. **Dual-currency + token firewall** holds across both planes: only Chips wagerable, Kash
   non-wagerable and non-transferable, token never touched.
