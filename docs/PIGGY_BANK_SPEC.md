| `Piggy:MinFlyAmount` | `100000` | chips banked since the last celebration before the fly plays; below it the bar just fills |
# Piggy Bank — spec

*Status: accrual + state + admin building (steps 1–3). The break is behind the unbuilt IAP receipt seam (step 4).*

## 1. What it is

A chip pack with a price, whose **unlock is paced by play**. The bank fills as the player wagers; when it is full they
may buy it, at the best chips-per-currency rate in the store.

Everything below follows from one sentence: **chips are only ever minted when someone pays.** Accruing mints nothing —
the number on the bar is a promise, not a balance. That is why the accrual rate can be generous, why the accrual needs
no ledger, and why the single thing that must never ship is a free break.

> ⚠️ A free or "reward" break turns this into a chip faucet on top of the loyalty-store issue in
> `ECONOMY_CENTRAL_BANK_SPEC.md`. Not "cheap" — free. The gate is in code (`PiggyService.BreakAsync`), not in policy.

## 2. Accrual: wager, not loss

The player banks a percentage of **clean wager** (the earned, non-gifted stake) on every settled round.

**Per-round loss accrual was rejected.** A player betting 100 over 100 hands wins ~49 and loses ~51: gross losses
≈ 5,100, net loss ≈ 200. Accruing a share of each *losing* round pays against 5,100 — the bank fills ~25× faster than
the house won, and fill speed becomes a function of luck, so two players who played identically get the offer at wildly
different times. `Mode` keeps `Loss` and `Both` as options because "get some back" consoles well, but the default is
`Wager`.

Three guards:

| Guard | Why |
|---|---|
| Clean wager only | Gifted chips already can't earn XP or LP; the same two-account farm applies here. The games already pass a gifted-excluded figure. |
| Daily cap (`MaxAccrualPerDayPercent`) | The floor on "slowly". Without it a whale fills the bank before lunch and the offer stops being an event. |
| Chips only, never Tokens | NON-NEGOTIABLE #2. Enforced at the wallet boundary on the break. |

Accrual **stops at full**. Silently discarding overflow teaches players that playing while full is wasted, which is the
opposite of the intended pull.

## 3. Where it hooks in

The per-round accrual seam already exists and is called identically by all three games:

```
BlackjackTableManager.AccrueLoyaltyAsync(userId, cleanWager, roundId)
ThreeCardPokerTableManager  — same shape
VideoPokerService           — same shape
```

The piggy is a fourth accrual beside progression / VIP / loyalty / missions: one wrapped call per game, its own DI
scope, and — like the others — **best-effort**. A piggy failure logs and returns; it can never fail a settle, because
the wallet has already moved the player's money by then.

Not inside `WalletService`. Every game would get it for free, but the wallet is the one component that has to stay
boring, and a bonus feature must never be able to fail a money path.

### Idempotency

A Redis `SETNX piggyacc:{roundId}:{userId}` guard, 30-day TTL — the same mechanism `LoyaltyService` uses. One Redis
call, no database round trip on the hot path.

**No per-round contributions ledger.** It was in the first draft and was dropped deliberately: accrual is not a money
movement, so a duplicate can only unlock the offer slightly early — it cannot create chips. A row per hand for a
non-money event is a large table earning nothing. The **break** keeps a full audit row, because that one *is* money.

## 4. Schema

### `PlayerPiggyBanks` — one row per player

| Column | Notes |
|---|---|
| `UserId` | unique |
| `Tier` | index into the config's tier table; rises with player level |
| `Amount` | what is banked now |
| `MaxAmount` | the tier's capacity, **snapshotted** — a config edit must not strand a player's full bank |
| `LifetimeAccrued`, `BreaksCount` | telemetry / analytics |
| `AccrualDateUtc`, `AccruedToday` | the daily cap, without a second table |
| `RowVersion` | `DateTime?` (MySQL `timestamp(6)`) — never `byte[]` |

### `PiggyBreaks` — one row per conversion

`UserId`, `Tier`, `Amount`, `PriceSku`, `PurchaseId` (**unique** — the receipt/order id, which is what makes a retried
purchase pay once), `Status`, `CreatedAt`, `CompletedAt`, `RowVersion`.

Reserve → validate → grant → complete, in one transaction, the same shape as a daily claim, paying out through
`IRewardGrantService` under the key `piggy:{breakId}` so the credit is audited like every other reward.

## 5. Config

Defaults in `PiggyConfig`, overridable live from the `khela:settings` Redis hash (the admin dashboard's Settings page),
so tuning needs no deploy.

| Key | Default | Meaning |
|---|---|---|
| `Piggy:Enabled` | `false` | ships dark |
| `Piggy:Mode` | `Wager` | `Wager` \| `Loss` \| `Both` |
| `Piggy:WagerRatePercent` | `50` | % of clean wager banked |
| `Piggy:LossRatePercent` | `0` | % of a losing round's net loss |
| `Piggy:MaxAccrualPerDayPercent` | `25` | % of capacity per day — the floor on fill time |
| `Piggy:MinBreakPercent` | `100` | must be full to buy |
| `Piggy:CycleHours` | `72` | hours to buy a full bank AFTER the player is shown it (see §8). 0 = never expires |
| `Piggy:BypassPurchase` | `false` | ⚠️ dev only, mirrors `Rewards:BypassAdForMissedDays` |

### Tiers

| Tier | Min level | Max amount |
|---|---|---|
| 1 | 1 | 250,000 |
| 2 | 10 | 500,000 |
| 3 | 25 | 1,000,000 |
| 4 | 50 | 2,500,000 |

Calibrated from the low stake tier (`MinBet` 1,000 / `MaxBet` 10,000): ~2,000 a hand × ~60 hands ≈ **120,000 handle a
session**, so 50% banks ~60,000 a session and tier 1 fills in ~4 sessions. The daily cap holds the minimum at 4 days
whatever the stake. Higher tiers give a bigger bank, not a faster one.

For scale: loyalty is 1% of clean wager (100 chips per LP), so this is 50× that — worth checking the two against each
other once both are live.

## 6. API

- `GET /api/piggy` → `{ enabled, amount, max, percent, canBreak, tier, priceSku, accruedToday, dailyCapReached }`
- `POST /api/piggy/seen` → the player is looking at a full bank; starts the countdown (see §8)
- `POST /api/piggy/break` → receipt in, chips out, bank reset to the next tier *(step 4, blocked on IAP)*

**Not** folded into the blackjack board snapshot, despite the first draft saying so. The snapshot is built per table
and broadcast on every change; a per-player piggy read inside it is a database hit in the hottest loop the server has
(and the lobby/driver hot path is exactly where this codebase has been burned before). The client refreshes the piggy
on round end, next to the wallet refresh it already does.

## 7. Later

- **Value guard**: once IAP SKUs exist, the admin refuses to save a piggy whose chips-per-currency is worse than the
  best chip bundle. "Best value" is the whole promise, and it is the kind of thing that quietly breaks when someone
  tunes a bundle six months later.
- Break animation + full-bank notification badge on the client.
- Per-tier price SKUs, and a second bank tier unlocked by VIP rather than level.

## 8. The window (countdown)

A full bank does not wait forever — but the clock does not start when it fills. It starts when the player is **shown**
the full bank, and runs for `Piggy:CycleHours` (default **72** — three days). Miss it and the bank resets to zero and
starts filling again.

That ordering is the whole design. A window that ran while the player was offline would take away an offer they were
never given, and someone who opens the game to find a piggy that expired last Tuesday learns that filling it is
pointless. The deadline is on the **decision**, not on the play; the fill itself is paced separately by the wager rate
and the daily cap.

Three moments, all on `PlayerPiggyBanks`:

| Column | Set when | Meaning |
|---|---|---|
| `ReadyAtUtc` | accrual crosses the buy threshold | it is buyable; nothing is running |
| `SeenAtUtc` | the client posts `/api/piggy/seen` | the player has been shown it — the clock starts |
| `ExpiresAtUtc` | with `SeenAtUtc` | seen + `CycleHours` |

All three are null while the bank is filling, and all three are cleared on expiry.

### Why `/seen` is its own endpoint

`GET /api/piggy` never starts a clock. A read happens for all sorts of reasons the player never witnesses — a HUD
refresh after a round, a screen they never scrolled to — and starting a deadline on one of those burns a window nobody
was shown. The client posts `/api/piggy/seen` at the moment it renders the ready state, and nowhere else. Repeat calls
are safe: only the first sighting starts the clock, so re-opening the screen cannot push the player's own deadline back.

The failure modes are asymmetric, which is why it is built this way round: a client that forgets to call `/seen` leaves
an offer standing too long, whereas a GET that started the clock would silently destroy full banks players never saw.

### On the client

`TimerRunning` — not `SecondsLeft` — decides whether the timer label is visible. A full bank the player has not been
shown yet has no clock, and a zero `SecondsLeft` would read as "expired" when it means "not started". `SecondsLeft` is
sent alongside `ExpiresAtUtc` so a device with a wrong clock still counts down correctly.

Expiry is evaluated **lazily**, on every read and every accrual — no sweeper job to lag behind or fall over. An expired
bank found during a GET is settled there and then; a settle racing it simply wins.

The reset re-reads the player's tier, so someone who levelled up during a window gets the bigger bank next time.
Mid-window capacity stays snapshotted — a bank cannot move its own goalposts while it is being filled.

Nothing is lost that ever existed as money: an expired bank was only ever a promise. `ExpiredCount` and
`LastExpiredAmount` are recorded per player purely to tune the window — a lot of expiries means the deadline is tighter
than players can decide in, not that they need more pressure.

## 9. The return-from-a-session celebration

When the player comes back to Home, chips fly into the pig and the bar fills **with** them — but only if the session
was worth it. Below `Piggy:MinFlyAmount` (default 100,000) the bar just fills quietly.

The threshold is a **running total**, not a per-visit one. `PiggyWidget._celebratedTo` only advances when chips
actually fly, so three short sessions accumulate into one celebration instead of three shrugs. A per-visit delta would
mean a player who plays in small bursts never sees the animation at all, however much they banked.

It reuses `RewardFly` — the same component the pass and daily ladders pay through — under its own reward id
(`Piggy` by default, and deliberately **not** `Chips`, which belongs to the wallet counter and would swallow the
pieces). The widget needs a `RewardFlyTarget` with the matching id.

The bar is handed to the burst while it plays: `RewardFlyTarget.BurstProgress` walks it from where it was to the new
value, one slice per chip, and each landing pokes `IdleMotion` so the pig reacts to being fed. `BurstEnded` — and
leaving the screen — snaps it to the truth, so a burst cut short can never strand the bar half full.

Where the chips come FROM is authored (`Fly From`), defaulting to the widget itself, which reads as coins tumbling out
and back into the pig. It must not be the chip balance: chips flying off the player's balance says they were charged,
which is the opposite of what happened.

The baseline is not persisted. On a cold start the first snapshot is a starting position, not an event — whatever is
in the bank was earned before the player was watching, and celebrating it on every launch would make the animation
meaningless.
