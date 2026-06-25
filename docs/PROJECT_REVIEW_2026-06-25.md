# Khela — Whole-Project Review (2026-06-25)

Deep review across four lenses: backend architecture, money-path/security, game-engine
correctness, and client + launch-readiness. Static analysis (no `dotnet build`/Unity run).
File:line references are to the backend (`khela/Khela.Game`) and client (`khela/Khela.Play`).

---

## Verdict

The project is **healthier and more complete than the docs claim**, with a money path and game
engine that are genuinely well-engineered for a pre-launch codebase. There are **no
architectural blockers**. What stands between here and a real-money test build is a short,
concrete list: **two committed secrets, one blackjack money exploit, the IAP feature, a
deployable non-localhost backend (which is also the APK bug), and prod-environment hardening +
legal sign-off.** None of these are deep rewrites.

If you fix the secrets and the re-split bug, build the IAP step, and deploy the backend over
HTTPS, you can run the small-audience retention test the plan is built around.

---

## What's strong (don't re-touch)

- **Wallet ledger is the centerpiece and it's correct.** `WalletService.ApplyAsync`
  (`Services/Wallet/WalletService.cs:85-156`): pessimistic `SELECT … FOR UPDATE` on the exact
  row, idempotency dedup on `(WalletId, CorrelationId)` *inside* the lock, signed-delta invariant
  by construction, overdraft guard, and the dual-currency legal guard (Bet/Win rejected on
  non-Chips/Coins, line 92). Only one place in the whole codebase writes `PlayerWallet.Balance`
  (line 125) — every other mover goes through `IWalletService`. No double-spend / double-credit
  vector found under concurrent + adversarial scrutiny.
- **Settlement is rule-derived, not read off a mutable mirror.** Part A pays the rule gross and
  uses the engine delta only as a `settle_mismatch` tripwire. Part B
  (`SettlementReconciliationService`) is genuinely **default-off** (three independent checks) and
  **idempotent** (reuses the exact `:pay` correlation id; orphan refunds use a distinct id behind
  liveness checks). Safe to keep.
- **Provably-fair shuffle is real.** HMAC-SHA256 commit-reveal (`ProvableShuffle.cs`,
  `DeterministicRng.cs`): per-table secret server seed, committed hash exposed, rejection-sampled
  keystream, Fisher-Yates — **no `System.Random` anywhere**. `PrevHandHash` chaining is wired and
  active (tamper-evident per-table chain).
- **Engine math is textbook-correct.** S17 dealer, 3:2 naturals, natural-vs-21 distinction,
  equal-value split (incl. K+Q), split-aces-one-card, double-after-split, insurance + dealer peek.
- **Round driver can't double-settle** (three guard layers: `:settling:` NX, `:settled:` NX,
  idempotent `:pay`), and re-reads table state under the lock so it never acts on stale Redis.
- **Stale-seat reaper exists and is money-safe** — the known "player stuck in seat" is a *client*
  heartbeat/leave gap, not a server gap; the server reaps ~30s after heartbeat stops and never
  pulls a live in-round stake.
- **Client slice is real** (Unity 6.3, URP): Boot → Home → Lobby → Table fully assembled, server-
  authoritative card rendering honored (cards built only from `BoardSnapshot`), physical chip-drop
  betting, REST + both SignalR and polling transports behind `IBlackjackHubClient`.

---

## BLOCKERS before taking real money

### B1 — Committed secrets (do this first; it's a credential leak)
- **Weak, committed JWT signing key**: `appsettings.json:19`
  `"SecretKey": "YourSuperSecretKeyHere_ChangeInProd"`. Anyone with the repo can forge a valid JWT
  for **any user id** → drain/grant any wallet, impersonate any player. Replace with a high-entropy
  secret from env/secret-store.
- **Committed MySQL root password**: `appsettings.json:4` (`password=reza@865174`, user `root`).
  Rotate it and move it to secrets. Treat the current value as burned.
- Scrub both from git history, not just the working copy.

### B2 — Blackjack re-split creates an un-funded, paid hand (house-loss exploit)
- `SplitAsync` keys the wallet debit as `bjr:{roundId}:{seat}:sp{handIndex}`
  (`BlackjackTableManager.cs:959`). A re-pairable split (the fresh card pairs `c1` again) lets the
  player call `SplitAsync(0)` a second time → **same correlation suffix** → `DebitAsync` sees the
  id as already-applied and returns the existing txn **without charging**
  (`WalletService.cs:108-113`), while `Player.Split` still adds a real, payable hand
  (`Player.cs:213-220`). The settle tripwire can't catch it (rule gross and mirror agree on the
  un-funded hands). Low-frequency but a genuine exploit, and **untested**.
- Fix: make the split/double correlation suffix unique per stake (include `Hands.Count` or a
  monotonic stake counter), and/or cap re-splits. Same fragile pattern exists for double
  (`dd{handIndex}`, `:847`) — safe today only because a hand doubles once. Add a re-split test.

### B3 — IAP purchase flow + server receipt validation → credit Chips (the actual Phase-0 gate)
- **Zero code** on either side: no `UnityEngine.Purchasing`/`StoreController` on the client, no
  `PurchaseController`/Apple-Google receipt validation on the server (`TransactionType.Purchase`
  exists in the enum but nothing produces it). This is the "take money" step and it hasn't started.

### B4 — Deployable, non-localhost, cleartext-safe backend (this is ALSO the APK bug)
- **APK table-scene bug root cause (high confidence):** `Assets/Resources/AppConfig.asset:15`
  hardcodes `baseApiUrl: http://localhost:5044`, consumed by the REST client, the SignalR hub URL
  resolver, and auth. On a phone `localhost` is the phone → every call fails → no board snapshot
  ever arrives → table connect throws into a silent `catch` (`TableController.cs:108`), camera/
  buttons stay dead, status shows "Connecting…" forever. Works in editor only because there
  `localhost` *is* the dev backend.
- Compounding: **no `AndroidManifest.xml` / `network_security_config.xml` anywhere** — Android 9+
  blocks cleartext HTTP/WS by default, so even a plain-`http://<ip>` dev box would be refused.
- Fix order: (1) point `AppConfig.asset` at a real **HTTPS** host; (2) if testing over plain HTTP,
  add `usesCleartextTraffic`/network-security config under `Assets/Plugins/Android/`; (3) surface
  the table `catch` to an on-screen banner so failures are visible, not a frozen camera;
  (4) confirm the Table prefab's `hubComponent` is the **polling** client for the first device
  build. Then deploy the .NET backend + MySQL + Redis behind TLS with the SignalR `?access_token=`
  hook live on the public host.

### B5 — Prod-environment hardening
- **Admin policy is wide-open in Development**: `Program.cs:103-112` returns `true` for any
  authenticated user when `ASPNETCORE_ENVIRONMENT=Development`. If a build is ever deployed as
  Development (common misconfig), the reconciliation money-healer + admin reports endpoints are
  open to everyone — combined with B1 that's a full ledger compromise. Ensure prod sets the
  environment correctly; consider failing closed if `Admin:UserIds` is empty in non-dev.
- Tighten dev-only CORS `AllowAnyOrigin` (`Program.cs:149-150`) and `AllowedHosts: "*"` before prod
  (both already flagged in-code).

### B6 — Legal / compliance gate (per PROJECT_PLAN §9)
- Age-gating, geo-restriction (Bangladesh specifically), and **lawyer sign-off** before real money.
  Non-negotiable and jurisdiction-specific. Not a code task — schedule it now so it isn't the thing
  that blocks an otherwise-ready build.

---

## SHOULD-FIX (not money-blocking, but soon)

- **Visible connection-failure UX at the table** — today a dead backend is a silent
  `Debug.LogError` and a frozen scene; testers will report "broken" with no signal. Surface it.
- **Gate scene entry on auth readiness** — the Boot 8s timeout currently lets an *unauthenticated*
  player into Home/Lobby/Table where everything fails silently. Gate on `AccountManager.IsReady` or
  show an explicit offline screen.
- **Drop the stray `Microsoft.AspNetCore.SignalR` 1.2.0 package** (`Khela.Game.csproj`) — legacy
  standalone metapackage, redundant inside an `Sdk.Web` app.
- **Settle holds a 30s table lock across N sequential wallet credits + retries + SaveChanges**
  (`BlackjackTableManager.cs:336`) — plausibly tight at a full contended table. Money stays safe if
  the lock lapses (the NX guards hold) but a torn table blob is possible; consider lock-renewal or a
  larger settle TTL.
- **Two-bucket clean/tainted wallet doesn't exist yet** — gifts currently grant **full**
  progression value. When `PROGRESSION_SPEC` is built, `EarnedChips`/`GiftedChips` is a money-path
  change; until then, be aware gift-laundering of progression is currently unguarded (low impact
  while there's no XP/VIP system to farm).

---

## MINOR / polish

- `DeckHash` fingerprints a re-derived shoe, not the dealt object (`BlackjackTableManager.cs:704`);
  matches today by determinism, but dev-rigged hands won't verify — add a comment.
- `preSettle` summary `FinalValue`/`Bust` reads `Hands[0]` only for split seats (audit summary
  field only; per-hand `GameHandParticipant` rows are correct).
- Dealer flip is positional `Cards[1]` not `IsCardUp`-driven (`BlackjackGame.cs:103`).
- `CradGames` folder is a misspelling of `CardGames` (folder/csproj name only; assembly + namespaces
  are correct — purely cosmetic, low priority to rename given .sln path coupling).
- `BlackJackGame` casing, controller field-naming inconsistency, over-modeled `ApplicationUser` PII.
- Intentional seams (not bugs): AI moderator stub, leaderboard seal/payout TODO, password-reset
  email unwired, cosmetics-entitlement placeholder.

---

## Docs to correct (they understate the code)

The `CLAUDE.md` "smaller fixes" and `PROJECT_PLAN.md` "next steps" are **stale** — several listed
TODOs are already done. Update so the docs stop misrepresenting reality:
1. Doubled `namespace CardGames.Blackjack` — **gone** (only one decl, `BlackjackGame.cs:10`).
2. `GameHandSnapshot` persistence — **wired** (Settle-stage snapshot + SHA-256 hash,
   `BlackjackTableManager.cs:185-208`); only the Deal-stage snapshot is unused.
3. `PrevHandHash` chaining — **active**, not unused.
4. Client **seat-pick** — **done** end-to-end (`JoinTableRequest.SeatNumber` →
   `LobbyTableCard.JoinSeat` → `GameSession` → `TableController.MySeat`).
5. Client **result banner** — **done** (`RoundResultBanner.cs`); remove the stale
   `TableActionBar.cs:160` comment.
6. Disambiguate "social/chat/profile **endpoints** exist" from "the spec'd **hardening** is done" —
   PROFILE_SOCIAL_SPEC/CHAT_SPEC describe unbuilt hardening (moderation, bio/status, block-aware
   reads) on top of controllers that already exist. A reader today would think social is finished.
7. Note in the plan that XP/VIP/Loyalty (`PROGRESSION_SPEC`) and the two-bucket wallet are
   design-only / not started.

---

## Recommended immediate sequence

1. **B1 secrets** (an afternoon) — rotate JWT secret + DB password into env/secret-store, scrub
   git history. Cheapest fix, worst exposure.
2. **B2 re-split bug** (small, contained) — unique stake correlation suffix + a re-split test.
3. **B4 connectivity** — stand up an HTTPS dev backend, point `AppConfig.asset` at it, add the
   Android network config, add a visible connection-error banner. This unblocks *any* on-device
   testing and proves the APK fix.
4. **B3 IAP** — the real feature work; build client purchasing + server receipt validation →
   `WalletService.Credit` (Purchase txn). Largest item.
5. **B5 prod hardening** + **B6 legal** in parallel with B3.
6. Then ship to the small Bengali/South-Asian audience and measure retention + willingness to pay.

Backend severity tally: **Blockers** B1, B2 (+ feature-gate B3, infra B4/B5, legal B6).
**No architectural blockers.** The foundation is sound; the remaining work is hardening, one
exploit, and the money-in feature.
