# Marketing Strategy + Khela Marketing Engine (KME) — Spec

*Global mobile social casino, solo dev, ASP.NET Core .NET 8 + MySQL + Redis + SignalR + Unity.
Strategy first, then the in-house automation tool that executes it. Honors all CLAUDE.md
NON-NEGOTIABLES — critically: **every reward grant is a money path and goes through
`IWalletService` with a `CorrelationId`.** Figures flagged [D] documented / [V] vendor claim /
[A] anecdotal. Verify vendor pricing + ad policy live before committing.*

---

## 0. Three findings that shape the entire plan

**1. You cannot buy this funnel.** Casino CPI ~$1.50–$5.00 and rising, and casino has the **worst
paid:organic ratio of any genre (11.05)** [D]. Payback windows of D180+ are normal. A solo dev
cannot win at stage 1 (installs). **The tool must earn its keep on stages 3–13 — owned channels,
zero marginal cost.** Organic/creator/referral/lifecycle is the strategy, not the fallback.

**2. Paid UA is administratively gated before it's financially gated.** Google Ads requires
**social-casino certification per targeting group**, and *one account cannot hold certifications for
two different social-casino targeting groups* — a **global** launch means **separate ad accounts per
region**, and standards change **26 Aug 2026** [D]. Meta requires 18+ gating + disclaimers. TikTok
effectively bans the category. This further forces the organic-first posture.

**3. Your architecture is a structural advantage nobody else has.** Because gameplay is
**server-authoritative** and every balance change already flows through `IWalletService`, you can
emit **economy and money events server-side** — unspoofable, un-blockable by ATT/ad-blockers, never
lost to app kills. Most studios can't. **Emit economy events from the backend; only UI/session/funnel
events from Unity.** This is the highest-leverage decision in the whole spec.

---

## 1. The funnel (what we optimize, in order)

| # | Stage | Metric | Benchmark | Notes |
|---|---|---|---|---|
| 1 | Install | CPI | casino $1.50–5.00 [D] | Not our lever |
| 2 | Registration | completion % | — | **Device-guest auth removes this drop-off entirely — real advantage** |
| 3 | **FTUE complete** | tutorial completion | **only ~30% complete** [D] | **Biggest single leak in the funnel** |
| 4 | Activation | % reaching N settled hands in session 1 | define own (e.g. 5 hands) | 75% churn <24h [D] |
| 5 | D1 retention | D1 % | all-genre ~27%; top quartile ~31% [D] | Gate for paid UA |
| 6 | D7 | D7 % | median 3.4–3.9%; top quartile 7–8% [D] | |
| 7 | D30 | D30 % | 75% of games <3% [D] | **Card/casino is among the BEST genres for long-term retention** [D] — our genre pays off here |
| 8 | First purchase | payer conversion | **social casino ~12% ever purchase** [D] (vs 2–5% casual) | |
| 9 | Repeat purchase | days-to-2nd | — | Get #2 within 7d of #1 or they lapse [A] |
| 10 | **Golden Pass renewal** | R1 renewal % | **15–40% churn at first monthly renewal** [D]; retention ~doubles after R1 | **Highest-leverage automation we own** |
| 11 | Whale | % rev from top 1–2% | 60–90% [A]; SciPlay AMRPPU **$126.23/mo** [D] | |
| 12 | Churn | lapse rate | signals appear **5–7 days before** departure [V] | Intervene at T-5..T-2 |
| 13 | Win-back | resurrection % | reactivation costs **1/5–1/10 of new acquisition** [D] | |

**Gate:** no paid UA until **D1 ≥ 25–30%** and ~2% pay. Below D1 15%, buy nothing.

---

## 2. Build vs Buy — the stack

**Principle: rent the pipes, own the player-data layer.** SaaS CRM models "users who fire events."
Our business lives in a **double-entry ledger**. Every valuable segment is a SQL join over
`PlayerWallet` / `WalletTransaction` / `GameHandParticipant`. And **no vendor can be inside a wallet
transaction** — so the campaign engine is in-house *by construction*.

| Capability | Decision | Tool | Cost |
|---|---|---|---|
| Game KPI dashboards (DAU/retention/funnels) | **BUY** | GameAnalytics free (**no MAU cap**; free A/B + remote config) | $0 |
| Google-ecosystem analytics | **BUY** | Firebase Analytics | $0 |
| Product analytics (Mixpanel/Amplitude) | **SKIP** | — | — (event caps punish per-hand logging; our SQL is better) |
| Economy / whale / ledger analytics | **BUILD** | SQL views over MySQL | $0 |
| BI dashboard UI | **BUY (OSS)** | Metabase/Grafana on a **read replica** | $0 |
| Attribution (MMP) | **BUY** | **Tenjin free — 2,000 paid conversions/mo forever**, organic uncounted | $0 |
| Creator/referral attribution | **BUILD** | Short-link + Play Install Referrer | $0 |
| Push transport | **BUY** | **FCM (free, unlimited)** | $0 |
| Push **targeting/scheduling** | **BUILD** | `CampaignService` | $0 |
| In-game inbox | **BUILD** | Table + SignalR + Unity panel | $0 |
| **Reward grants** | **BUILD (forced)** | `IWalletService` + `CorrelationId` | $0 |
| Client cosmetic config | **BUY** | Firebase Remote Config (100k fetch/day free) | $0 |
| **Economy/payout config** | **BUILD (forced)** | Config table + Redis (rule #1: server-authoritative) | $0 |
| Crash / performance | **BUY** | Crashlytics + Perf Mon | $0 |
| Transactional email | **BUY** | SES / Resend / Postmark | ~$0–20/mo |
| Braze / CleverTap / Branch / Adjust / AppsFlyer paid | **REJECT** | — | $15K–200K+/yr |

**Never build:** APNs/FCM plumbing, crash symbolication, SKAN postback decoding, ad-fraud detection,
email deliverability, charting UI.

**Total recurring cost of the recommended stack: ~$0/month** until >2,000 paid installs/mo.

**Why free-tier SaaS CRM can't do our job:** OneSignal free = **2 data tags, 6 segments, 1 journey**.
We need "VIP tier 3 AND chips < 500 AND no purchase 9d AND favourite game = blackjack." GameAnalytics
free is **aggregate-only** (user-level = $499/mo PipelineIQ). Braze starts ~$60K/yr.

---

## 3. The Khela Marketing Engine (KME) — architecture

```
Unity client
 ├─ Firebase SDK ──► Analytics · Crashlytics · Perf · FCM token · RemoteConfig (cosmetic only)
 ├─ GameAnalytics ─► retention/funnel dashboards
 ├─ Tenjin (phase 1) ─► paid-install attribution
 └─ REST + SignalR ─► BACKEND (the only source of truth)

ASP.NET Core
 ├─ IWalletService (unchanged, authoritative, idempotent)
 ├─ PlayerEvent spine ─┬─► MySQL append-only log
 │                     └─► Redis stream ─► fan-out worker ─► GA / Tenjin S2S
 ├─ PlayerProfileProjection (materialized rollup, ticker-refreshed)
 ├─ SegmentService     (saved SQL predicates, membership w/ enter/exit timestamps)
 ├─ CampaignService    (journey runtime: trigger → delay → branch → A/B → exit)
 ├─ ArbitrationLayer   (frequency caps, quiet hours, priority)  ◄── build BEFORE campaign #2
 ├─ Channel adapters   (Inbox/SignalR · FCM · Email · WhatsApp later)
 ├─ OfferService       (targeted, expiring, idempotent grants → IWalletService)
 ├─ ConfigService      (economy config, server-authoritative, Redis-cached)
 └─ CreatorService     (codes · install-referrer decode · payout ledger)

MySQL read replica ──► Metabase = the real analytics UI
```

### 3.1 Components (build order)

**1. Event spine.** Append-only `PlayerEvent` (already partly exists via `GameHandActions`).
Everything else is derived. Server emits `hand_settled`, `bet_placed`, `purchase_completed`,
`session_start`, `balance_low`. **Client is never the source of truth for anything analytical** —
same principle as rule #1.

**2. `PlayerProfileProjection`** — materialized per-player rollup, refreshed by a
`BlackjackRoundDriver`-style ticker:
`R/F/M scores · spend tier · lifecycle stage · favourite game (GameDefinition key) · streak state ·
balance bucket · habitual play hour · timezone · locale · channel reachability (push token/email) ·
churn-risk score · holdout group`.

**3. `SegmentService`** — segments as saved predicates over the projection. **Store membership with
enter/exit timestamps** (you need "entered segment X" as a trigger).

**4. In-game inbox — build BEFORE push.** `PlayerMessage` table + SignalR + Unity panel. No OS
permission, no opt-in decay, 100% deliverable, and it can carry a **claimable reward**. Highest-ROI
channel we own.

**5. `CampaignService`** — journeys as *data, not code*: trigger (event or schedule) → delay →
branch (opened/ignored, converted/not) → A/B split → exit conditions.

**6. `ArbitrationLayer`** — sits between journeys and channels. **Build this before your second
campaign or campaigns will fight each other and burn opt-ins.**
- Global cap **3/day**, tiered: actives daily-tolerant, at-risk every-other-day, lapsed ≤2/week
- **Quiet hours 22:00–07:00 user local time** (store timezone, not country)
- Per-campaign cap + priority ordering
- **Send-time optimization**: modal hour of last 30 sessions (cheap to compute, high value)

**7. `OfferService`** — an offer is a **targeted, expiring, idempotent grant**.
`CorrelationId = $"campaign:{campaignId}:player:{playerId}"`. Tap the notification twice → no double
mint. This is a money path: same idempotency + audit as a settle.

**8. Holdout groups — non-negotiable for measurement.** Reserve a **universal 5–10% holdout** that
receives zero lifecycle messaging, plus per-campaign holdouts. Without this you cannot distinguish
"the campaign worked" from "they were coming back anyway" — and every vendor benchmark in this doc is
inflated by exactly that confusion.

**9. Auto-reports** — post-campaign/event: participation %, ARPDAU delta, session-length delta, and
**post-event D7 vs pre-event D7** (the guard rail: any event spikes; a good event doesn't borrow from
next week).

---

## 4. Campaign catalogue

### Tier A — build first (ranked by evidence)

| # | Campaign | Trigger | Channel | Evidence |
|---|---|---|---|---|
| A1 | **Onboarding rescue** | `install+10min` AND `tutorial_complete=false` AND `!session_active` | Push | 26% early-churn reduction, 10.7% conversion [V] |
| A2 | **Streak-at-risk / daily bonus** | `streak≥3` AND `unclaimed` AND `hours_to_break≤4`; or `bonus_available` AND `18h idle` at habitual hour | Push | **7.4% CTR vs ~1% gaming baseline** [V]; 3x DAU case [V] |
| A3 | **Out-of-chips offer** | `balance < min_buy_in` AND `session_active` | **In-app** (not push) | Retiming offers to resource-depletion moved conversion **3.2%→5.8%** [D] |
| A4 | **First-purchase starter** | `!payer` AND `sessions≥3` AND (`balance=0` OR milestone) AND `shown<3/7d` | In-app | Cutting entry price raised conversion **+5% AND ARPPU +40%** [D] — cheap first purchases make *better* payers |
| A5 | **Golden Pass R1 defence** | `renewal-3d` AND `days_active_cycle<5`; `renewal-1d` + dunning; cancel→1/7/30d ladder | Push+Email | R1 churn 15–40% [D]. **Mid-cycle value nudge**: a subscriber not claiming dailies is the best non-renewal predictor |
| A6 | **Win-back ladder** | lapse × payer-status matrix (see below) | Push→Email→Retarget | Reactivation is 1/5–1/10 the cost of acquisition [D] |
| A7 | **Whale alert** | `lifetime_spend≥tier` OR `spend_velocity_7d>3x baseline` OR (`whale` AND `idle≥2d`) | **Human task queue** | Automate the *alert*, not the message |

**A6 win-back matrix** (segment by lapse depth × payer status — treating all lapsed users the same is
the documented failure mode [D]):

| Lapse | Never-payer | Past payer | Whale |
|---|---|---|---|
| D3 | Push: free chips | Push: personalized to last game | escalate to human |
| D7 | Push + email; rewarded-video chips | Push + email; modest bundle | VIP host message, bespoke |
| D14–30 | Email; new-game announcement | Email + retarget; biggest offer | manual |
| D30–60 | Retarget only (cheap) | Retarget + email | manual |
| D60+ | **Suppress** | Event-triggered only | manual |

Segment by **churn cause**, not just recency: content-exhausted → new game/event; progression-blocked
→ chip grant; attention-shifted → distinctive event; frustrated → "we fixed X" *before* any promo [D].
Returners need **catch-up mechanics** + a "what changed" recap or they re-churn.

### Tier B — after A
Welcome series (3 msgs/week 1) · D1/D3/D7 return triggers · **cross-game cross-sell**
(`games_played=1 AND sessions≥10` → "you've only tried Blackjack") — our most under-used axis ·
level-up congratulation + offer (attach offers to highs, not just lows) · social triggers
("your friend beat your best hand") · D7-lapse exit survey.

### Ranked by conversion [evidence-weighted]
1. Streak/daily reminders (~10x baseline CTR) · 2. Onboarding rescue · 3. Contextual out-of-resource
offers · 4. Subscription R1 defence · 5. LiveOps-tied re-engagement · **6. Generic "come back" push —
worst; produces notification-disables, not returns** [D].

---

## 5. Segmentation

**Four axes:** RFM (recency/frequency/monetary — churn models hit **93% F1 with RFM vs <75%
without** [D]) × spend tier (non-payer/minnow/dolphin/whale **+ subscriber**, behaviorally distinct)
× lifecycle stage (early D1–7 / mid D7–30 / endgame D30+) × **game affinity** (blackjack vs Teen
Patti vs slots vs Hold'em — drives which event to notify, which cross-sell, likely which geo).

**Ship these 10, not 40:** New/tutorial-incomplete · New/activated-never-paid · Habit-forming
(streak≥3) · Engaged non-payer · First-time payer · Repeat payer · Whale · Subscriber (+renewal-soon,
low-engagement sub-split) · At-risk · Lapsed (× payer × 7/30/60d).

Segmented LiveOps lifts ARPDAU **15–30%** over one-size-fits-all [D] — so the event config schema
must be `(event_id, segment_id) → parameters`, **not** `event_id → parameters`.

---

## 6. Churn scoring (rules-based v1 — ship before any ML)

Signals ranked by documented power: recency drift vs personal baseline · **session frequency drop
≥40%** [V] · **broken streak** (cleanest binary for a social casino) · balance zero + no refill ·
lengthening inter-purchase interval · session length decline · **social-graph decay — friends going
inactive is the *earliest* signal** [V] (we already have friends/gifts/chat — free, high-signal) ·
progression stall.

```
risk = w1*(days_since_session / personal_median_gap)
     + w2*(1 - sessions_7d / sessions_prior_7d)
     + w3*(streak_broken)
     + w4*(balance_zero_no_refill)
     + w5*(days_since_purchase / personal_median_purchase_gap)   // payers
     + w6*(fraction_friends_inactive_14d)
```
Bucket low/med/high. **High-risk payers → human queue; high-risk non-payers → automated offer.**
Retro-validate at 90 days, then swap to logistic regression / GBM on the same features (literature:
RFM-cluster-then-classify wins [D]).

---

## 7. Channels

| Channel | Use | Notes |
|---|---|---|
| **In-app** | Anything in-session: low-balance offers, first-purchase, streak status, opt-in pre-prompt | Highest conversion, no opt-in cost, never causes uninstalls. **Never interrupt an active hand.** |
| **Push** | Return triggers | Reality check: gaming push CTR **0.5–1.0%** [D]; 63.5% opt-in and falling. **32% cite notifications as top uninstall reason** [D] |
| **Email** | Reaches uninstalls + push-opt-outs; win-back, receipts, renewals, whale | Device-guest auth means **no email by default — capture softly at first purchase + Golden Pass signup** |
| **WhatsApp/SMS** | Whales + subscription dunning only | Expensive; WhatsApp dominant in South Asia |
| **Retargeting ads** | 60d+ lapsed only | **Re-engagement creative, never UA creative** [D] |

**Opt-in:** never fire the OS prompt cold — **in-app pre-prompt after a positive moment** (first win
/ first daily bonus claim), framed as "we'll remind you when your free chips are ready."

**Deep links:** apps using deep links roughly **double retention at D1/D7/D30** [D] — probably the
highest ROI-per-engineering-hour item here. Link to the *specific* surface (the table, the offer, the
unclaimed bonus), never the home carousel. Note **Firebase Dynamic Links is EOL** — use Tenjin DDL or
Android App Links + iOS Universal Links.

---

## 8. Event taxonomy (server-side where it matters)

**Rules:** `snake_case`, `object_action`, past tense. **Prefer properties over event proliferation** —
`bet_placed { game_key }`, never `blackjack_bet_placed`. This mirrors the config-driven
`GameDefinition`/`GameCatalog` design, so adding roulette adds **zero** new events.

**Server-emitted (authoritative, unspoofable):** `bet_placed`, `hand_settled`, `balance_changed`,
`balance_low`, `balance_zero`, `purchase_completed`, `subscription_started/renewed/cancelled`,
`reward_granted`, `level_up`, `vip_tier_changed`, `chips_to_kash_exchanged`.
**Client-emitted (funnel/UI only):** `first_open`, `tutorial_start/step/complete`, `session_start/end`,
`store_viewed`, `offer_shown/clicked/dismissed`, `ad_watched`, `invite_sent`.

**Volume warning:** a blackjack hand ≈ 5 events; 1,000 DAU × 60 hands = **9M events/mo** — this is why
Mixpanel/Amplitude free tiers are unusable and why we keep raw events in **our own MySQL** and send
only **session/economy summaries** to third parties.

**KPIs:** DAU/MAU stickiness · D1/D7/D30 · session length/freq · ARPDAU/ARPU/ARPPU · payer conv % ·
LTV · CPI/ROAS · churn · K-factor. Cohort everything; compute LTV from retention-curve integration.

---

## 9. Organic + creator automation (what's *legally* automatable)

- **TikTok: DO NOT BUILD.** TikTok's Content Sharing Guidelines explicitly forbid our exact use case:
  *"Not acceptable: A utility tool to help upload contents to the account(s) you or your team
  manages"* [D]. **Manual only.**
- **YouTube Shorts: the best automation target.** Quota model changed — `videos.insert` now has its
  own bucket at **100/day, cost 1 per call** [D]. You can lawfully auto-upload up to **100 Shorts/day,
  free.** Pair with auto-generated **big-win clips** from gameplay.
- **Facebook/Instagram Graph API** — scheduling permitted; FB is the key channel for the South-Asian
  slice. **Reddit** — manual/community-rules bound.
- **Creator CRM (BUILD):** discovery list → contact/outreach tracking → templates + follow-ups →
  **per-creator code + short link** → Play Install Referrer decode → installs/creator → chip-reward
  payout ledger. No vendor sells "affiliate program paying in non-cashable chips."
- **Referral loop (BUILD):** double-sided reward, **gated behind low balance** (tie it to a real need
  — the HQ-Trivia lives trick), deferred deep link attribution, K = i×c measured per channel, fraud
  checks (device correlation, reuse the guest device id).
- **ASO automation (BUILD thin):** keyword rank tracking, review monitoring + alerts, A/B test
  tracking, weekly digest.
- **Reporting automation:** daily/weekly KPI digest + **anomaly alerts** (retention drop, revenue
  drop, crash spike) to your phone.

---

## 10. LiveOps = the marketing engine

**84% of mobile game IAP revenue flows through games running active LiveOps** [D]. For social casino,
*LiveOps is not a layer — it is the product*.

**Cadence:** casual-tier = **12–20 events/month in overlapping layers**. 72-hour Fri→Sun event is the
gold standard. **12–24h breathing room** between competitive events — "when events never stop,
participation becomes obligation, and obligation becomes churn" [D]. Predictable rhythm > volume.

**Calendar:** Daily login + escalating 7-day streak; happy hour on login · Weekly Fri 18:00–Sun
tournament + Wed milestone chain · Monthly Golden Pass season reset + collection album · Seasonal
(Diwali/Eid/Lunar New Year/Christmas — also the cheapest organic UA moments) · Always-on piggy bank +
rolling offer + starter pack.

**Event archetypes that port to cards:** leaderboard tournament · milestone bar · **collection album**
(thematically perfect for a card game) · rolling offer · choose-your-own-offer · **happy hours
triggered by login** (a CRM trigger as much as an event) · task chains · partner/friend events ·
**piggy bank** (exceptional fit for a chip economy).

**Success criteria (auto-reported):** participation 40–60% of DAU · session length +15–25% · ARPDAU
+20–40% · **post-event D7 no decline vs pre-event** (the guard rail) · LTO conversion 3–8% [D].

**Every event needs matched CRM:** start → push actives + email lapsed + in-app in-session; ending in
4h → push participants who haven't claimed.

---

## 11. Guardrails (non-negotiable)

1. **All marketing grants are Chips/Coins only.** No offer, bonus, VIP comp, or win-back grant may
   ever touch the **token**. Enforced at the same wallet boundary as gameplay.
2. **Every reward is idempotent** (`campaign:{id}:player:{pid}`) through `IWalletService`.
3. **Marketing rewards stay off the XP/VIP progression track** (same rule as gifted chips and bot
   tables) — otherwise campaigns buy rank and the "level is earned" integrity breaks.
4. **Responsible play, from day one.** The whale-targeting machinery in this doc is exactly what
   regulators and press scrutinize. Build **spend-velocity ceilings** and a **self-exclusion /
   cool-off flag that hard-suppresses all monetization messaging** in the arbitration layer. Cheaper
   to have and not need. Lawyer-review item alongside the dual-currency guardrail.
5. **Ad compliance:** 18+ targeting, "not real-money gambling / no prizes of value" disclaimers, no
   real-casino brand names, never advertise the token as redeemable. Separate Google Ads accounts per
   social-casino targeting group (global = multiple accounts).
6. **Templates and targeting rules live in the repo**, version-controlled and lawyer-reviewable — not
   in a vendor dashboard with no diff history.

---

## 12. Build order

**Phase 0 — integrate (≈1 week, $0/mo):** Firebase SDK (Analytics + Crashlytics + Perf + FCM +
RemoteConfig) · GameAnalytics SDK · **Metabase on a MySQL read replica** ← where real analysis lives ·
**no MMP yet**.

**Phase 0.5 — build these three, in order:**
1. **Event spine** (`PlayerEvent` + Redis stream + fan-out worker)
2. **In-game inbox** (before push — no permission, carries claimable rewards)
3. **`CampaignService`** + `ArbitrationLayer` + `OfferService` (wallet-idempotent)

**Phase 1 — first campaigns:** A1 onboarding rescue → A2 streak → A3 out-of-chips → A4 first-purchase
→ A5 Golden Pass R1 → A6 win-back. Holdouts on from campaign #1.

**Phase 2 — growth:** Tenjin (week before first paid campaign) · Google Ads social-casino
certification (**start months early**) · creator/affiliate module · YouTube Shorts auto-upload ·
referral loop.

---

## 13. Definition of done

- Event spine live; economy events emitted **server-side**; client only sends funnel/UI events.
- `PlayerProfileProjection` + 10 segments computing on a ticker; membership has enter/exit timestamps.
- Inbox + FCM + email adapters behind the **arbitration layer** (global cap, quiet hours in local
  time, priority, send-time optimization).
- Every reward grant idempotent through `IWalletService`; token never granted; marketing rewards
  excluded from XP/VIP.
- **Universal 5–10% holdout** live; every campaign auto-reports lift vs holdout.
- Deep links wired to specific surfaces; opt-in via in-app pre-prompt after a positive moment.
- Churn score computed; high-risk payers routed to a human queue.
- Metabase dashboards: funnel, cohorts, ARPDAU, chip sink/faucet balance, whale table.
- Anomaly alerts (retention/revenue/crash) firing to phone.
- Responsible-play suppression flag + spend-velocity ceiling implemented.
- `dotnet build` green; deliberate migrations; no NON-NEGOTIABLE weakened.

---

## Sources (key)
GameAnalytics 2025 Benchmarks · FoxData 2026 UA Cost Benchmarks (casino CPI, paid:organic 11.05) ·
SolarEngine first-purchase study (3.2%→5.8%; price cut → ARPPU +40%) · Pushwoosh 2025 push benchmarks
+ justDice/Beach Bum cases · CleverTap RFM docs (10-segment model) · Playio LiveOps (84% IAP, cadence,
event success criteria) + re-engagement · Kumo.ai churn signals (5–7 day window, social-graph decay) ·
RFM-LIR churn literature (93% vs <75% F1) · SciPlay Q3 2025 (AMRPPU $126.23) · IconEra social-casino
monetization (~12% ever purchase) · Tenjin/AppsFlyer/Singular/OneSignal/Firebase/GameAnalytics pricing
pages · TikTok Content Sharing Guidelines · YouTube Data API quota docs · Google Ads Gambling & Games
policy (Aug 2026 update) · Meta social-casino ad policy.
