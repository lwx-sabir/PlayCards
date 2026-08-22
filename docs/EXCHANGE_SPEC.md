# Currency exchange — A → B at an admin rate (built 2026-08-23)

*Status: BUILT — server + admin + client data layer. The exchange SCREEN is Reza's (`PlayCard.Exchange.ExchangeState` carries the data).*
*Decision: DECISIONS E-03 (Chips → Kash, 1,000,000 : 1, one way). Economy: `khela-economy-audit` — this pair is THE chip sink the peg rests on.*

## 1. What it is
A generic, admin-authored set of **pairs** — any wallet currency for any other at a fixed **rate** — that a player can run from the
client. Today's catalog has one pair (chips → Kash); tomorrow's can have Kash → Gems, Gems → Chips, Coins → Chips… **no code**:
a pair is a row in the admin, the rate and every limit are knobs. Server-authoritative end to end: the client picks a pair and an
amount; cost, eligibility, caps and the two wallet movements are the server's.

## 2. The rate, and why it is stored that way round
`FromPerUnit` = **units of FROM per ONE unit of TO** (1,000,000 chips per 1 Kash ⇒ `1000000`). The player chooses the **TO**
amount (a multiple of `Step`, ≥ `MinTo`); **cost = toAmount × FromPerUnit, exactly** — no floor, no remainder, nothing minted.
A rate stored as "Kash per chip" would be 0.000001 and every quote would round somewhere. Both legs are ledger rows of type
`TransactionType.Exchange = 7` (append-only), paired by correlation ids `xchg:{exchangeId:N}:d` (debit) / `:c` (credit).

## 3. Guardrails (validator, fail-closed; `ExchangeCatalogTests`)
- **Tokens can never be on either side** (`RewardCurrencies.IsAllowed` — Chips, Coins, Gems, Kash only). The legal line.
- From ≠ To; rate > 0; step > 0; min > 0 and a multiple of step; caps ≥ 0 and ≥ min when set; window sane.
- **One enabled pair per route** (two rates for Chips → Kash would make "the rate" ambiguous); disabled duplicates are fine.
- **Every loop must LOSE value**, not just A→B→A: the validator walks every directed cycle of the enabled-pair graph (≤ 4
  currencies; `ExchangeCatalog.Cycles`) and refuses the first whose product of rates is ≤ 1 (a lap returns `1/product`) — so
  Chips → Kash → Gems → Chips can't double chips per lap with no pair having a reverse. The admin page lists every cycle's factor.
- **The wallet's scale (`decimal(18,4)`)**: MySQL silently *rounds* a finer amount, and a debit that rounds to 0.0000 against a
  credit that lands is minting. So `step` and every cap are whole 0.0001s of TO, and the cost quantum `step × rate` must be
  ≥ 0.0001 FROM and itself a whole 0.0001 — then every aligned amount's cost is exact. A rate finer than a wallet unit is fine
  *with* a step that makes the cost exact (0.000002 Kash per chip, step 50 ⇒ 0.0001 Kash). `Refusal` re-checks exactness at
  runtime regardless of the catalog. `CurrencyExchanges.RateFromPerUnit` is `decimal(28,10)` (informational; the amounts are
  the truth).
- Pair keys are canonicalised (trimmed) on validate, so `Find` and the admin's forms always match the stored value.
- Admin **notes** (not refusals) against the store ladder: bought chips → Kash at X Kash/$ vs the Kash lane direct (E-03: ~20× worse,
  never an arbitrage); a Kash → Chips pair that would beat the chip packs is flagged as a **bridge** (`iap-shop-design`: no bridges).

## 4. Per-player rules (`ExchangeCatalog.Refusal`, pure)
Window (`FromUtc`/`ToUtc`), `MinLevel`, amount > 0, step-aligned, ≥ `MinTo`, ≤ `MaxToPerTx`, `DailyCapTo` (UTC day) and
`LifetimeCapTo` in TO units — counted from the player's Completed `CurrencyExchanges` rows. Then "Not enough {From}" from the wallet.

## 5. The money path (`ExchangeService.ExchangeAsync`)
1. Request = `{pairKey, toAmount, requestId}`; **`requestId` is the idempotency key** (a fresh Guid per tap; `ExchangeState` makes it).
   A Completed row with that id ⇒ the original outcome is replayed (`Replayed = true`), nothing moves.
2. Switches (`Exchange:Enabled` on the settings hash + the catalog's own `Enabled`), pair enabled, refusal rules, cost.
3. Reserve a `CurrencyExchanges` row (unique `(UserId, RequestId)` is the mutex against a double tap). A re-driven Pending/Failed
   row (nothing moved for it) is **pinned** to the exchange its id was first used for — same pair, same currencies, same TO amount
   (otherwise a Failed row reserved under a cheap pair could complete under an expensive one at the cheap cost) — and is
   **re-priced at the current catalog** with its clock restarted, so Failed rows can't be banked at an old rate or outside a later
   day's cap. Daily usage is judged on **completion** time.
4. Both wallets created **before** the transaction (reward-claim-latency rule), then **one DB transaction**: debit FROM
   (`InsufficientFundsException` → rollback, row Failed, "Not enough …"); then the caps **re-checked under the FROM-wallet lock
   with a `FOR UPDATE` read** of the player's rows (two taps with different request ids both pass the plain pre-check; only one
   holds the wallet row at a time, and the second sees the first's commit here — without this a daily cap is a suggestion);
   credit TO, row Completed, commit. `WalletService` joins the ambient transaction; its own idempotency on the correlation ids
   makes any retry exact.
5. Result carries both new balances and every balance (`Balances`, `NewChipBalance`, `NewKashBalance`) — the client routes it
   through `BalanceChangingAsync`, so every HUD repaints from the server's numbers.

## 6. Surfaces
- **Game API** `api/exchange`: `GET` catalog (pairs + my availability/usage + balances), `POST quote`, `POST` exchange, `GET history`.
- **Admin ▸ Exchange**: kill switch; pairs table + editor (currencies, rate, step, min, max/tx, daily + lifetime caps, level, window,
  title/description, enabled, sort); Add pair; backups (before every save, download/restore); raw JSON; reset to E-03 defaults;
  economy notes. **Ledger**: every exchange, 30-day totals per pair (sunk / issued), search by player / ids.
- **Client**: `BlackjackRestClient.GetExchangeCatalogAsync / ExchangeQuoteAsync / ExchangeAsync / GetExchangeHistoryAsync`;
  `PlayCard.Exchange.ExchangeState` (Instance, `RefreshAsync`, `Pairs`, `BalanceOf`, `QuoteAsync`, `ExchangeAsync` — one in flight,
  `Changed` / `Exchanged` events); `ExchangeResultData : IChipBalanceResult`. Khela.Common DTOs in `Khela.Common.Exchange`.
- **Data**: Redis doc `khela:exchange` (overlay shape, `ExchangeCatalog.Defaults()` fallback, 15 s cache); table `CurrencyExchanges`
  (migration `AddCurrencyExchanges`).

## 7. Not done / later
- The client screen (Reza). A "how many?" stepper against `MinTo/Step/MaxToPerTx`, the quote line, the button on `ExchangeState.Busy`.
- The settings transfer group (`SettingsController.TakeDoc`) does not yet include `khela:exchange`; the document travels by backup
  download/restore until it does.
