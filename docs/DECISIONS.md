# Decision Log — Khela World

*One line per load-bearing decision, with the reason. Purpose: decisions survive context loss and
don't get re-litigated. **Append only** — if a decision is reversed, add a new entry marking the old
one SUPERSEDED rather than editing it.*

*Seeded 2026-08-19 from the strategy/design conversations. Dates before that are approximate
("~2026-06/08") because they were made in chat and back-filled here.*

**Format:** `ID · Date · Area · Decision · Why`

---

## Product & strategy

| ID | Date | Decision | Why |
|---|---|---|---|
| P-01 | ~2026-06 | **Social casino, not a memecoin.** Game earns first; token is Phase 2. | Real product, legal, and the token needs revenue to back it. |
| P-02 | ~2026-06 | **Phase sequencing: 0 = fun + first payers · 1 = grow revenue · 2 = token.** No blockchain code until real growing revenue. | Prevents building the speculative layer before the earning layer. |
| P-03 | ~2026-08 | **Global product, not Bengali-only.** | Game lineup is the universal casino canon (blackjack, 3CP, slots, video poker, Hold'em). A regional product would have led with Call Break / 29 / Rummy — it didn't. |
| P-04 | ~2026-08 | South-Asian market is a **launch beachhead (tactic), not the product's identity.** | Cheap CPI + reachable community for a solo dev; product/art/brand stay global. |
| P-05 | ~2026-07 | **Khela World is ONE app**; games are rooms/tables inside it. Per-game store listings point at the same build, defaulting the carousel to that game. | The walkable social world requires a single app; separate apps would fragment liquidity and the economy. |
| P-06 | 2026-08-19 | **Blackjack ships first.** | Vs-dealer ⇒ a single player is a complete game ⇒ zero liquidity requirement. Acquire users one at a time. |
| P-07 | ~2026-08 | **Texas Hold'em is built LATE.** | Compounds every hard problem at once (side pots, largest collusion surface, worst liquidity) exactly when the audience is smallest. |
| P-08 | ~2026-08 | **Steam is not a channel.** Web build is the PC door. | Steam's audience is hostile to F2P + gambling-adjacent + mobile ports; zero organic pull for the genre. |
| P-09 | ~2026-08 | **Differentiator = chips have terminal purpose** (Chips → Kash → virtual world). | The genre's structural weakness is meaningless chips; incumbents can't copy it without cannibalising their own P&L. |

## Economy & money

| ID | Date | Decision | Why |
|---|---|---|---|
| E-01 | ~2026-06 | **Dual-currency legal firewall:** only Chips/Coins are wagerable; token never bet or won. Enforced at the wallet boundary. | The non-cashable prize is what makes this a social casino, not gambling. |
| E-02 | ~2026-08 | **Three currencies:** Chips (wagerable, non-cashable) · **Kash** (virtual-world only, non-wagerable, non-transferable, in-game only) · Token (Phase 2, separate island). | Clean separation; you never *win* Kash at a table, so the gambling loop never touches the "valuable" currency. |
| E-03 | ~2026-08, rate fixed 2026-08-22 | **Chips → Kash exchange, one-way, 1,000,000:1** (1M chips = 1 Kash; Kash lane sells 100 Kash for $1.99 ⇒ 1 Kash ≈ $0.02, so converting bought chips (5M/$1.99) is ~20× worse than buying Kash directly — never an arbitrage). **Built 2026-08-23** as the generic A→B exchange system — `docs/EXCHANGE_SPEC.md`, Admin ▸ Exchange. | Gives chip-grinding a purpose (the differentiator) *and* is the biggest chip sink. The punishing rate also does legal work by minimising Kash won via gambling. |
| E-04 | ~2026-08 | **Kash never bridges to the Token.** Token is never buyable with Chips/Kash and never won. | Gambling → Token → cash would collapse the entire firewall. |
| E-05 | ~2026-08 | **Kash is never giftable/transferable.** Items bought with Kash may be giftable — but only as **buy-as-a-gift** (fresh purchase directed at a friend), never an inventory transfer. Gifts are a value dead-end (no re-gift, no sell-back). | Currency is fungible/liquid ⇒ instant grey market. Items are specific/illiquid ⇒ safe, and gifting is a strong social/viral lever. |
| E-06 | ~2026-08 | **NO peer-to-peer chip transfer, ever.** Gifts are **server-minted faucets** (sender is never debited). | Verified true in `GiftService` today by accident; now policy. Removes the entire chip-dumping and chip-RMT vector at zero cost. |
| E-07 | ~2026-08 | **Two-bucket wallet:** `EarnedChips` vs `GiftedChips`. Gifted chips grant **zero** XP/VIP/loyalty. Bets spend earned-first; payouts keep the stake's gifted ratio. | Closes the gift→clean laundering path and keeps progression honest. |
| E-08 | ~2026-08 | **P2P item trading allowed** (Reza's call, overruling initial caution) — but **items↔Chips only**, meaningful trade tax, soulbound prestige tier, no Kash peer trades, all trades logged into the collusion graph. | P2P economies are the strongest social/communication engine; the risk is managed by closing the *value exit*, not the market. |
| E-09 | ~2026-08 | **Elastic chip supply is acceptable, but must be run deliberately** — peg administered prices (Kash rate, XP floors, buy-ins, store SKUs) to a supply index and re-denominate as a batch. | Neutral money holds only if *everything* reprices; uneven inflation is the actual danger. |
| E-10 | ~2026-08 | **Golden Pass** = monthly subscription, daily collect, paid track rich / free track thin, optional rewarded ads; paid tier removes ad-gates. Pass rewards weighted to Kash + cosmetics, excluded from XP/VIP rank. | Recurring revenue + daily habit engine without buying progression rank. |

## Games

| ID | Date | Decision | Why |
|---|---|---|---|
| G-01 | ~2026-08 | **Teen Patti = the real PvP pot game** (blind/seen/chaal, side-show, pot escrow). | It's the culturally authentic version and the first PvP/pot engine, which Hold'em later reuses. |
| G-02 | ~2026-08 | **3 Card Poker = vs-dealer**, reuses the blackjack pattern; may **dual-skin as house-banked "Teen Patti"** via `GameDefinition`. | Live studios do exactly this. Cheap second game, no pot/collusion/liquidity cost. |
| G-03 | ~2026-08 | **Video poker is slots-family** (single-player vs paytable); social is a bolted-on score layer (tournaments/leaderboards/jackpot). | Be explicit that it is not a multiplayer table before committing. |
| G-04 | ~2026-08 | **Pot = per-hand escrow ledger account** (`player → pot:{handId} → winner`), not a table integer. | Conservation falls out for free; disconnects are trivially correct; reuses the idempotent wallet. |
| G-05 | ~2026-08 | **Hunting:** Kash buys bullets (1 Kash = 20, capped near the useful ceiling), **shared** animals, **damage-share** kill credit, spawn scales **sublinearly** with bullets/field size, payout in Chips. | Damage-share removes kill-steal rage; sublinear scaling keeps value-per-Kash declining so whales can't farm cheap chips. |
| G-06 | ~2026-08 | Hunting rewards sit on their **own progression track** (don't feed wagering XP/leaderboards). | Closes a farm path around the tables. |

## Technology

| ID | Date | Decision | Why |
|---|---|---|---|
| T-01 | ~2026-06 | **Server-authoritative gameplay.** Server deals/shuffles/settles; client renders + sends actions only. | Non-negotiable; the card asset pack is a *renderer*, never a deck. |
| T-02 | ~2026-06 | **All money through `IWalletService`** — idempotent on `CorrelationId`, `SELECT … FOR UPDATE`, signed delta, `BalanceBefore/After`. | Verified safe under adversarial/concurrent review. |
| T-03 | ~2026-08 | **FishNet** for the realtime world — **not** Photon Quantum or Fusion. | Quantum is deterministic-ECS lockstep for frame-precise combat; would force an ECS rewrite, fight the GameObject asset stack, and cost per-CCU for a problem we don't have. FishNet is free, GameObject-based, mobile/PC/web. |
| T-04 | ~2026-08 | **Two networking planes:** tables = ASP.NET REST+SignalR (money authority) · world/hunting = FishNet Unity headless server (gameplay only). The world server **never mints currency**. | Keeps the whole money path inside the one audited, idempotent, legally-firewalled place. |
| T-05 | ~2026-08 | **BoZo modular characters** for avatars — over UMA2/o3n Jane&John, and over Synty characters. | Stylized (cohesive with Synty world), no UMA runtime mesh-combine cost on mid-tier Android, modular parts feed the cosmetics system, URP-ready. |
| T-06 | ~2026-08 | **Synty stylized environments** for the walkable world (curated — cull the toy props); table scenes keep their own look with 2D avatar portraits. | Deliberate two-context art split; stylized reads premium on phones and avoids the semi-real uncanny/perf trap. |
| T-07 | ~2026-08 | **PolygonNature (Synty, classic) for mobile hunting scenes**; **rejected** BK "Pure Nature"/"Pure Nature 2" packs (949 MB–1.9 GB, realistic, Unity Terrain, no LODs). | Mobile-first means building to the phone ceiling and scaling *up*, never dragging a PC pack down. |
| T-08 | ~2026-08 | **Invector** (owned) for the humanoid character controller; **Malbers only if animals ship** (hunting). | Invector is humanoid + mobile input; Malbers is an animal controller with nothing to drive until hunting exists. |
| T-09 | ~2026-08 | **Animancer for table/event animation; Mecanim (via Invector) for world locomotion.** Cards move by tween, not skeletal animation. | Each tool in its lane; they live on different GameObjects so they never conflict. |
| T-10 | ~2026-08 | **Addressables + Play Asset Delivery**; world scenes are **remote on-demand bundles**, never in the base APK. A shared group prevents duplication. | Small install, free hosting on Android, and content updates without a store review. |
| T-11 | ~2026-08 | **Mobile-first budget:** build to a mid-tier Android ceiling; PC/web get more frames/resolution/post, **not** more content. | One content pipeline; avoids discovering on-device that the game doesn't fit. |
| T-12 | ~2026-08 | **Graphics: 3 URP quality tiers + Auto**, render scale and 30 fps default on mobile (60 opt-in), colour-grade LUT protected at every tier. | Render scale + framerate are the big mobile levers; the grade is where "premium" lives. |
| T-13 | ~2026-08 | **Regional servers: shard the GAME, never the WALLET.** One global primary DB for accounts/wallet; regional game servers + read replicas. | Multi-master money = split-brain = double-spend. Turn-based tables tolerate the cross-region write latency. |
| T-14 | ~2026-08 | **Launch single-region, design region-aware.** Add regions only when players/latency demand it. | 3 regions on day one triples cost and splits liquidity across a base too small to fill one lobby. |
| T-15 | ~2026-08 | **Bots for liquidity/seeding**, tagged as house bots — excluded from progression, leaderboards, payouts and collusion signals. Human-priority matchmaking so bots recede as real players arrive. Track **real-human concurrency separately**. | Legitimate in a non-cashable social game; but must not become a comfortable illusion masking flat PMF. |

## Clubs & social

| ID | Date | Decision | Why |
|---|---|---|---|
| C-01 | 2026-08-19 | **Clubs are a social layer over PUBLIC matchmaking.** No private club tables, no club-configurable stakes/rake/seating, no owner-distributed prize pools. | UKGC's top collusion signal is "players who frequently share tables" — a club manufactures that signal as normal behaviour. Every club-app disaster (Pokerrrr 2, PPPoker) traces to giving clubs economic control. |
| C-02 | 2026-08-19 | **Club XP accrues automatically from play; Club Fund donations are BURNED** (one-way sink) and the club receives system-minted perks. | Casuals contribute by existing; the sink is also the compliance firewall. |
| C-03 | 2026-08-19 | Club perks are **frictional, not multiplicative** (slots, cooldowns, capacity), capped at ~15–20% free-chip advantage; everything above that is cosmetic/status/access. | Avoids the rich-get-richer spiral; follows the CoC perk philosophy. |
| C-04 | ~2026-08 | **Leaderboards:** overall board ranks on **XP** (comparable, unbuyable, farm-resistant); per-game boards use fresh resettable metrics; Weekly + All-Time; Global/Friends/Country scope. | Never rank on chip balance (whale board) or net profit (collusion-farmable). |
| C-05 | ~2026-08 | **Stats: cross-game aggregate for identity + per-game breakdown for meaning.** Win rate and streaks are per-game only. | Blended win rate across blackjack/Teen Patti/slots is meaningless. |
| C-06 | ~2026-08 | **Leaderboard/stat storage:** cumulative latest-value table (all-time, never pruned) + a short-retention timestamped history table (daily/weekly/monthly range queries). No Redis/ZSET needed at launch. | Simple SQL; all-time never depends on history, so history can be pruned. |

## Marketing & growth

| ID | Date | Decision | Why |
|---|---|---|---|
| M-01 | 2026-08-19 | **Organic/creator/lifecycle first; paid UA last.** | Casino has the worst paid:organic ratio of any genre (11.05) and CPI $1.50–5.00; a solo dev cannot buy this funnel. |
| M-02 | 2026-08-19 | **Build the marketing engine in-house**; rent only the pipes (FCM, SES, Firebase, GameAnalytics, Metabase, Tenjin). | No SaaS can be inside a wallet transaction — reward grants must run through `IWalletService`. Free-tier CRMs cap at 2–10 data tags; our segments are ledger joins. |
| M-03 | 2026-08-19 | **In-game inbox before push.** | No OS permission, no opt-in decay, 100% deliverable, and it can carry claimable rewards. Gaming push CTR is only 0.5–1%. |
| M-04 | 2026-08-19 | **Universal 5–10% holdout from campaign #1.** | Without it you can't separate "campaign worked" from "they were coming back anyway." |
| M-05 | 2026-08-19 | **No TikTok auto-posting** (their guidelines forbid tools that upload to accounts you manage). **YouTube Shorts auto-upload is the automation target** (100/day, free). | Policy-documented. |
| M-06 | 2026-08-19 | **Don't chase 10k downloads directly** — prove D1 ≥25–30% and ~2% payer conversion at ~1k first. | Installs into an unproven loop are churned users and burned budget. |

## Brand & naming

| ID | Date | Decision | Why |
|---|---|---|---|
| N-01 | ~2026-08 | Brand root is **Khela**; ship as **Khela World** (never bare "Khela"). | `khela.app` is a live Bangladeshi **betting** brand that even offers blackjack — bare "Khela" undermines the non-cashable positioning, is generic-for-goods (weak trademark), and raises Apple 4.3 risk. |
| N-02 | ~2026-08 | Per-game store names: **`<Game> 3D — Khela World`**, keyword-first. **Drop "/Pro"**, standardise "3D" placement, **drop "Casino"** from the slots title (keep it in the keyword field). | Keyword-first for ASO; "Pro" implies paid; "Casino" raises age-rating/region drag. |
| N-03 | ~2026-08 | Always keep the "h" — **Khela**, never "Kela". | "Kela" = banana (Hindi) / crude slang (Assamese). |

## UI

| ID | Date | Decision | Why |
|---|---|---|---|
| U-01 | 2026-08-19 | **Three visual tiers on Home; exactly one Tier-1 element (PLAY NOW)** with enforced clear space. | ~18 elements at equal weight means nothing leads. |
| U-02 | 2026-08-19 | **Currency pill: `icon → number (with thousand separators) → large protruding +`.** | Icon-first identifies *which* currency; the `+` closes the reading order as the CTA. |
| U-03 | 2026-08-19 | **Hide a currency at zero** rather than showing a dead `0`. | A `0` balance reads as broken. |
| U-04 | 2026-08-19 | **Progressive disclosure by level** — a level-1 player sees ~6 elements, not 18. | Legible first session; each unlock is a reward beat. |
| U-05 | 2026-08-19 | **Badges mean "free thing claimable right now"**, max 2 on screen. | Otherwise badge blindness. |
| U-06 | 2026-08-19 | **Transient home elements use fixed slots + priority arbitration**, hard per-rail caps, slide-in (never fade-in-place), never spawn during touch/first 1.5s/transitions. | Layout instability breaks spatial memory and causes mis-taps; the number of LiveOps surfaces only grows. |
| U-07 | 2026-08-19 | **Table CTAs are world-space (diegetic), owned by the focused carousel table.** | Reads as part of the world; buttons travel with their table so appearance *is* the focus state. |

---

## Reversals / superseded

*(none yet — record them here rather than editing entries above)*
