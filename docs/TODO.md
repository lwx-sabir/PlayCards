# TODO — what is next

*Written 2026-08-23, when we parked the VIP redesign at step 4 and turned back to the store.
Owners: **R** = Reza, **C** = Claude. Specs, not this file, hold the reasoning — this is only the running order.*

---

## NOW — store design + client wire-up

The server side of the store is done and audited; the client has a data layer and no screen. That gap is
the whole of the current work.

- [x] **C — Shop screen wire-up, code side.** `ShopSection` (binds a lane's authored cards to the section's products,
      cloning extras so an admin-added pack needs no build) + `ShopScreen` (fetch, loading / store-off / platform-off,
      re-fetch after a granted purchase) + `StoreRestoreButton` (Apple requires a restore affordance and nothing
      exposed one). Reviewed adversarially; the two confirmed defects are fixed. **Not compiled — no Unity here.**
- [ ] **R — wire it in the Editor.** Per lane: `ShopSection` on the lane, the section key (`chips` · `kash` · `packs`
      · `vip` · `piggy` · `pass`), the cards dragged in ladder order. `ShopScreen` on the shop root. First compile
      will also generate the `.meta` files.
- [x] **C — card states.** Already covered by `StorePurchaseButton`: sale (price-off vs value-bonus, with the struck
      price and a server-clock countdown), two badge slots, bonus %, owned, unavailable, and the server's own reason.
- [ ] **C — the VIP lane.** Five VIP-P packs and the two boosters exist server-side; a `ShopSection` with key `vip`
      shows them, but the card wants a VIP-P icon and `amountCurrency = VipPoints`.
- [ ] **R — art + layout** for anything the wire-up finds missing (the cards are yours; I fill them).
- [ ] **R — decide the ladder's shipped prices.** Everything in `StoreCatalog` today is a dev-time number.

Rules that are already settled — do not re-litigate: single-screen ladder, **Chips and Kash as parallel lanes**,
**no currency bridges**, odds disclosed. See `docs/IAP_SPEC.md` and the shop-design notes.

---

## PARKED — VIP redesign (`docs/VIP_SPEC.md`)

Steps 1–4 are built, committed and DB-migrated. Only step 5 is unbuilt code.

- [ ] **R — lawyer nod** on "% of net daily winnings as a VIP cashback" (spec §4). This gates the whole step.
- [ ] **C — step 5, after the nod:** per-player-local-day accrual of clean net with the level stamped at accrual;
      `POST /api/vip/rebate/claim` releasing closed unpaid days; `TransactionType.VipRebate = 8`; the popup state
      the client reads on the way home.

### Decisions only Reza can make (spec §8)

- [ ] Season length. `Season:LengthDays` ships **0 = lifetime**, so no season ever rolls until it is set.
- [ ] The win % / rebate % / daily-cap numbers. Mine are deliberately conservative; retune against the
      **expected-return-per-1M** column in the admin, which is there to stop a +EV rung shipping by accident.
- [ ] Whether the LP → chips rate should be tier-dependent (one more column).

### Built but switched OFF — one click each

- [ ] **`lp_chips` exchange pair** ships disabled at a placeholder rate, so LP is currently unspendable.
      The admin shows the implied comp %; set the real rate, then enable.
- [ ] **`starter_pack`** ships `Enabled = false` — and it is now also the door into VIP 1.
- [ ] **Restart the server.** Nothing from steps 1–4 is live on the running process.

---

## BEFORE ANY REAL MONEY MOVES

- [ ] **R — Play Console / App Store Connect products.** The catalog now carries store ids that do not exist
      on either store yet, including five new VIP-P packs. Every enabled product needs its counterpart created.
- [ ] **R — the app has to be publishable** (internal test track) before store products can be tested end to end.

---

## SMALLER, STILL OPEN

- [ ] Seat-pick: `int? SeatNumber` on `JoinTableRequest`.
- [ ] Split-hand UI.
- [ ] Swap polling → Best SignalR as the default transport.
- [ ] Remove the doubled `namespace CardGames.Blackjack` in `BlackjackGame.cs`.
- [ ] Wire `GameHandSnapshot` persistence (schema exists, unwired); `PrevHandHash` round-chaining is unused.
