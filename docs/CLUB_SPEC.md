# Club System — Research & Build Spec

*Khela World club/clan system: members, club level, rankings from member activity, member benefits,
club competition, global showcase. Target: `khela/Khela.Game` + `Khela.Play`. Honors all CLAUDE.md
NON-NEGOTIABLES. Evidence flagged [DOC] documented / [ANALYST] research firm / [ANEC] unsourced.*

---

## 0. Two findings that shape the design

**1. The opportunity is real.** GameRefinery: **~70% of US top-grossing games have a guild feature**,
and guild mechanics *differentiate the top-100 from the top-200* — but **"the Casino category is a bit
behind other genres in utilizing communal mechanics"** [ANALYST]. Clubs are under-adopted in exactly
your genre. A social-casino retention teardown also found the club feature is the **only** feature
appearing in the D1, D7 *and* D30 buckets for both Slotomania and Huuuge [ANALYST].

**2. The structural danger — read this twice.** The UK Gambling Commission's RTS 11 names its **top
collusion detection signal** as *"players who frequently share the same tables"* [DOC]. **A club is a
feature whose entire purpose is to make people frequently share the same tables.** You would be
shipping a feature that manufactures your primary fraud signal as *normal behaviour*, destroying its
discriminative power.

That's not a reason to skip clubs. It's the reason for the posture below.

**Your current position is strong and worth protecting.** Verified in the codebase: `GiftService`
credits the recipient via `WalletService.CreditAsync(..., TransactionType.Bonus, ...)` with **no
corresponding debit of the sender** — gifts are a **faucet, not a transfer**. Combined with blackjack
being **house-banked** (losses go to the house, not to another player), **there is currently no
player-to-player chip transfer path anywhere in the system.** Poker-style chip dumping is therefore
economically *impossible* today. That property is your strongest anti-abuse control and it exists
by accident rather than by policy — write it down before clubs make it tempting to break.

---

## 1. The posture: clubs are a SOCIAL LAYER over PUBLIC matchmaking

**Decouple the club's social value from the club's control over the game.**

| Clubs GET | Clubs NEVER GET |
|---|---|
| Identity: name, emblem, tag, description | **Private club tables** |
| Chat / club wall | Seat control or "join my table" in PvP |
| Levels, rankings, divisions | Stake, rake, or payout configuration |
| Competitions built on **aggregated public-table results** | Chip distribution / a treasury the owner pays out |
| Cosmetic rewards, badges, club hall | Any transferable or convertible prize pool |
| Shared goals, member benefits (faucet-granted) | Anything that affects table odds |

You lose the "let's all play in our private room" fantasy. You keep essentially **all** of the clan
retention loop — which comes from belonging, obligation and shared progression, **not** from
co-seating — and you keep co-occurrence as a usable fraud signal, which is what makes detection
tractable for one developer.

**Why this matters, from the case studies:** Pokerrrr 2's ToS *"doesn't even prohibit collusion"* and
club owners set their own rake (reviewers set 25% with no warning) [DOC-trade]. PPPoker built the
right controls (GPS/IP/device restriction) but made them **club-owner-optional** — so owners simply
don't enable them. And a **bot-as-a-service industry markets "liquidity bots" directly to club and
union owners** [VENDOR]. The threat model isn't only "players cheat in clubs" — it's "**club owners
cheat, at scale, as a business.**" Every one of those failures traces to giving clubs economic
control. Don't.

---

## 2. Structure

| Parameter | Value | Rationale |
|---|---|---|
| **Member cap** | **30**, unlocked by level (10 → 20 → 30) | Shipped caps: CoC/Clash Royale/SWGOH **50**, RAID **30**, MSF **24**, **Slotomania 10** [DOC]. WoW academic: mean guild **16.8**, median **9**, **~25% monthly churn** [DOC]. Level-gated caps keep low-level clubs *dense*, not sparse. |
| **Design content for** | **5 actives** | Every club activity must feel good with five people. If it needs 20, it's a later feature. |
| **Roles** | Leader → Co-Leader → Elder → Member | The CoC 4-tier standard [DOC]. **Elder is cheap status, not power.** Never let a role kick a peer of equal rank (prevents co-leader coups — the #1 support ticket). |
| **Join modes** | Open (algorithmic gate) / Request / Invite-only | **Gate algorithmically wherever possible** — an unread request queue is how you leak new players. Auto-expire requests at 48h and suggest 3 alternatives. |
| **One club at a time** | Enforced | Universal across every app studied. Exclusivity is what makes membership mean anything. |
| **Creation cost** | Chip cost + level/tenure gate | A sink and a commitment signal (Huuuge charges **2,000,000 chips** [DOC]). Auto-archive clubs that never reach 5 members in 14 days. |

**Inactivity cascade** (mobile churn is faster than MMO norms — Guild Wars/WoW use 30d [DOC]):
- Member 14d → greyed in roster with "last seen"
- Member 30d → excluded from club XP decay and event participation requirements (don't let them drag the club down)
- Member 45d → auto-removed with a re-join grace token
- **Leader 21d** → succession cascade: most-active Co-Leader → Elder → Member; notify club. Push the leader at day 12: *"Your club needs you."*
- Club 0 activity 60d → hidden from browse; 90d → archived

**Anti-hopping: vest rewards, don't punish leaving.** Club-event rewards pay only to members present
at *both* event start and settle; club perks ramp in over 24–48h. Every studied app implements some
version of a leave penalty — Huuuge: *"League Points earned during the season will be subtracted from
the club's balance and irreversibly gone"*; Slotomania resets weekly progress; Bingo Blitz zeroes
Team Points [DOC]. **The leave penalty is the retention mechanism, more than the rewards are.**

---

## 3. Progression

**Two currencies of contribution — this is the key design:**

- **Club XP (automatic, from playing).** Hands played / clean wagered chips generate club XP with no
  action required. Casual members contribute **by existing**. Levels the club.
- **Club Fund (opt-in, chips donated).** A **one-way sink** — donated chips are **burned**, the club
  receives system-minted perks. Nothing that enters the Fund can ever come back out to an individual.
  This is simultaneously an economy sink, a monetization driver, and a **compliance firewall.**

Huuuge levels clubs on donations and adds **~+5 member slots per level** [DOC]; Big Fish Casino does
the same (donate chips daily → club level → features + member limit) [ANALYST]. Daily donation caps
prevent a single whale soloing the club to max.

**What levels unlock — copy the CoC perk philosophy.** Of CoC's six clan perks, **five amplify
*giving*, not power** (shorter donation cooldowns, higher donation caps, donation refunds, bigger
treasury); only one is a direct payout multiplier, and it's gated behind winning [DOC]. A high-level
club isn't *stronger* — it's **more generous with less friction.** That's the single most
transferable lesson for avoiding a rich-get-richer spiral.

So: club level unlocks **member slots, gift slots/day, faster gift cooldowns, club hall cosmetics,
competition tiers** — and only a small, capped chip bonus.

---

## 4. Member benefits

**The killer feature: Jackpot Share / passive win-sharing.** Huuuge distributes a slice of *any*
member's jackpot to **all** club members, scaled by club league + reward tier + progress, advertised
up to **8x** [DOC]. Slotomania: when any member hits a jackpot or completes a set, **every member
gets a coin gift** (24h expiry, collected from clan chat) [DOC]. This is the **"win even when you
don't play"** hook and it's the most-advertised club feature in the genre. Ship it.

Full benefit menu:
- **Passive share** — a faucet-minted cut when any member hits a jackpot/big win
- **Club chat / wall** — coordination + celebration
- **Daily chip gift** — **fixed-size, rate-limited, server-minted** (never "send N chips")
- **Shared Club Pot** — everyone's play fills it, everyone opens it
- **Club-only cosmetics** — table skins, avatar items, club hall decor, name frames
- **Club challenge** — "club collectively wins 500 hands this week"
- **Status** — club crest + tag on the nameplate, at tables and in the 3D world

### The rich-get-richer guard (mandatory)
1. **Perks are frictional, not multiplicative.** Cooldowns, slots, capacity — these have natural
   ceilings; multipliers don't.
2. **Hard-cap total economic value:** a max-level club member earns at most **~15–20% more free chips
   per day** than a clubless player. Everything above that ceiling is **cosmetic, status, or access** —
   which can inflate infinitely without breaking the economy.
3. **Bracket competition by tier, always** (§6).
4. **Score per-capita, not absolute** (§6).
5. **Everyone who participates gets something** — gap between 1st and last ≈ **3x, not 30x**.
6. **Seasonal resets** so month-one clubs don't own the ladder forever.
7. **Club benefits never touch the wager or the odds** (CLAUDE.md rule 2). Perks affect **free chip
   acquisition** only.

---

## 5. Competition — phased by population

**Ranked for a casual, cold-start audience:** co-op shared goal → division league → multi-club race →
head-to-head war → brackets (showcase only).

**Phase A — Weekly co-op goal (build first).** The Slotomania Clan Key model: shared bar, hit the
collective goal before the week ends, **everyone who met a low personal floor gets the chest** [DOC].
**No losers**, no matchmaking, works at *any* population size. Bingo Blitz and CoC Clan Games are the
same shape. This is what social-casino leaders actually ship.

**Phase B — Monthly Club League (once ~40+ active clubs).** CoC CWL structure [DOC]: groups of **8**,
round-robin, **6 divisions × 3 tiers**, **2 up / 2 down**, reward *rates* scale by division. Two
details to steal: a **hard floor** (never demoted below the bottom tier) and **asymmetric mobility at
the bottom** (3 promoted / 0 relegated in the lowest tier — the basement is an escalator, not a trap).
Huuuge's analogue: **1 League Point per 100,000 chips won**, Bronze → Master divisions, weekly season
resets [DOC].

**Phase C — Monthly multi-club race (only if B is healthy).** Monopoly GO Tycoon Racers: 4–5 clubs,
medals by finishing position, **double points in the final race**, and **auto-fill with random players
when short-handed** [DOC]. Multi-way softens losing ("we came 2nd" is a real outcome).

**Never a workhorse: brackets.** Half your clubs are eliminated after round 1 with nothing to do.
Use once per season as a **top-8 invitational** off the league table, purely for showcase.

**The cautionary tale:** Clash Royale's Clan Wars 2 used **absolute reward thresholds** — clans
without ~50 actives got *nothing*. Supercell publicly conceded they *"missed the mark"* [DOC].
Never use absolute thresholds; always normalize by active roster size; always give a floor reward.

---

## 6. Scoring fairness

```
ClubScore = Σ over members of min(memberEventPoints, CAP)
```
where `memberEventPoints` derives from **hands played and outcomes — never from chips purchased**,
and placement compares against a target scaled by `min(activeMembers, fieldedRosterSize)`.

- **Hard per-member cap, non-purchasable.** CoC Clan Games caps at 4,000 (now 10,000) pts/player and
  **enforces it with a carrot** — hitting your cap grants an extra reward pick [DOC]. Cap feels like
  an achievement, not a wall.
- **Or count only the top-N members** (best 15 of 30) — CoC CWL fields exactly **15 regardless of
  clan size** [DOC]. Perfectly size-normalizing; big clubs get depth, not more score.
- **Score win *or* lose.** Clash Royale's River Race awards Fame either way [DOC] — removes the
  "don't play, you'll cost us points" anxiety.
- **Why the caps matter, quantified:** a monetization consultant spent **$200 and 5.5 hours** in one
  3-day guild event and personally generated **44% of his entire guild's score**; he estimated the #1
  guild spent **$8,500–$10,000** [DOC, first-person]. Without caps, your club league is a wallet
  readout.
- **Anti-free-riding:** the Ringelmann/social-loafing literature finds effort drops with group size
  (groups of six shouted at **~36% of solo capacity**) and identifies **anonymity + unidentifiable
  contribution** as the drivers [DOC]. So: **make per-member contribution visible in the club UI** —
  the single highest-leverage, nearly-free anti-loafing feature. Gate rewards on a **low personal
  floor**, not a high quota (high quotas are the burnout mechanism).

---

## 7. Showcase & prestige

Prestige costs nothing to mint, can't be inflated by whales, and is what keeps top clubs playing
after they've saturated the economy. Cheapest → most valuable:

1. **Club tag + division icon next to the player name at the table** — highest impression volume in
   the entire game, every hand, every seat. This alone beats a leaderboard screen.
2. **Emblem builder** (shape × symbol × 2 colors — combinatorial, zero per-club art cost).
3. **Club profile page** — emblem, level, division, season ribbons, top contributors, founding date.
4. **Segmented leaderboards** — global top 100 **and** country/region **and** your division. Regional
   boards are what make a global launch feel winnable to everyone.
5. **Permanent seasonal trophies** ("Gold Division Champion — Season 3") + a **personal** cosmetic
   derived from your club's peak division (the CoC statue pattern: club rank → a persistent object
   others see in *your* space [DOC]).
6. **Global feed announcements** on promotion / season win / co-op clear — free social proof.
7. **Founder/veteran badges by tenure** — loyalty glue that also disincentivizes hopping.

---

## 8. Anti-abuse — the club-specific layer

**Don't run your existing win/loss graph over club games with public thresholds.** Co-occurrence in a
club is *expected*, so the raw signal saturates. The adaptations:

1. **Excess co-occurrence.** Compute the expected pair-seating rate given club size and activity;
   score **observed ÷ expected**. A pair sitting together far more than their own club's mixing rate
   predicts is the signal.
2. **Asymmetry, not volume, is the dumping signal.** Per pair: net chip flow and the **one-sidedness
   ratio** (|net| ÷ gross wagered). Honest play → ~0 net, high gross. Dumping → high |net|, low gross,
   few hands. Flag short-lifetime high-|net| accounts (dump-and-abandon mules).
3. **Club graph centralisation.** A healthy club is diffuse; a farming club is a **star topology**
   (many small consistent losers, one big consistent winner). **Rank clubs, not just players** —
   investigate clubs as units.
4. **Registration-time clustering.** Accounts created within minutes of each other that all land in
   one club is a bot-farm signature — **visible before a single hand is played.**
5. **Device / IP / install-ID collision share** within a club. Enforced **server-side and
   platform-wide**, never as a club-owner toggle (that's exactly PPPoker's flaw).
6. **Timing correlation** — co-login and co-join within seconds; identical session start/stop.
   Near-free to compute, very hard for a human ring to avoid.
7. **Action-timing homogeneity** for bot rings — you already persist move-by-move `GameHandActions`.
8. **Ranked review queue + human review, before any ML.** The academic state of the art (Greige et
   al., 173k players, AAAI-MAKE 2022) uses pair-level features + **Isolation Forest, unsupervised** —
   critical because you'll never have labelled colluders on day one — and outputs a **ranked queue,
   not an auto-ban**: *"failing to catch certain cheaters is still more desirable than banning a
   player that has been falsely labeled"* [DOC]. A sorted list of the 20 worst pairs per week is 90%
   of the value.

**Eligibility gates (kill bot-farmed alts economically):** club-competition contributions count only
after **N days of club tenure**, from accounts with **M days of age**, a **unique device**, and
meaningful play history. Cap any single member at **X% of club score**. Rate-limit club join/leave
(7-day cooldown, monthly join cap) — kills "swarm a club, win the weekly, disband."

---

## 9. Cold start

Below a few hundred DAU, an empty club browser is **worse than no feature**. Stage it:

- **Stage 0 — no clubs.** Ship chat + friends + fixed daily gift first. This builds the social graph
  clubs will later cluster on. (A roster is not a social network — WoW research found guild survival
  is predicted by **internal network density**; guilds of disconnected sub-cliques die [DOC].)
- **Stage 0.5 — random-team weekly event.** Candy Crush Friends' model: group **random players** into
  temporary teams with shared milestone rewards, no guild required [DOC]. Validates the co-op loop
  with zero cold-start risk and measures appetite before you build persistence.
- **Stage 1 — system clubs, auto-assignment.** Nobody creates clubs. Server maintains **pre-created,
  branded clubs themed on your 3D world districts** and auto-places every new player. **Fill to a
  floor before opening the next** — 3 clubs at 10 members, never 10 clubs at 3. Auto-place by
  **timezone/language first**, then level (a club whose members are never online together isn't a
  club). Players may switch freely.
- **Stage 2 — recommended clubs.** Replace raw browse with a ranked **"Recommended for you"** list
  (same language/timezone > similar level > recruiting > ≥5 actives > leader active in 48h). A browse
  list sorted by rank sends every new player to an elite club that rejects them.
- **Stage 3 — player-created clubs**, gated by level/tenure + chip cost.

**Presentation trick:** never show "4/30" on a near-empty club — show **"Active now: 4 · Recruiting."**
The denominator is what makes it look dead.

---

## 10. Build order

1. **Chat + friends + fixed daily gift** (no clubs) — builds the graph
2. **Random-team weekly event** — validates co-op, zero cold-start risk
3. **Clubs v1:** auto-assigned system clubs, roster, chat, crest/tag, **Club XP auto-earned from
   play**, club level → member cap + gift slots, roles + inactivity cascade, **per-member contribution
   visible**. Ship with a **90/10 holdout** to get a real retention read.
4. **Clubs v2:** shared Club Pot (equal payout), **Jackpot Share**, weekly co-op goal
5. **Clubs v3:** monthly Club League (bracketed, per-capita, floor rewards, seasonal soft reset)
6. **Clubs v4:** club hall in the 3D world, club cosmetics as the monetization surface, global +
   regional leaderboards
7. **Never:** private club tables, club-configurable stakes/rake/seating, owner-distributed prize
   pools, free-form chip transfer, absolute reward thresholds, leaderboards on absolute totals

---

## 11. NON-NEGOTIABLE guardrails

1. **No peer-to-peer chip transfer. Ever.** Gifts stay **server-granted faucets** (sender never
   debited — this is currently true in `GiftService` and must become policy, not accident). No club
   treasury members fund and the owner pays out. **"Club owner distributes the prize" is the single
   design decision that created the entire agent/union abuse economy.**
2. **Club Fund is a one-way sink** — donated chips are burned; the club receives system-minted perks.
3. **No club-configurable economic parameters** — stakes, rake, seating, payouts are server-owned.
4. **No private club tables in PvP games.** When Teen Patti/Hold'em ship, keep **random seating** and
   a **same-club co-seating cap** (k=1 safest, k=2 the retention compromise), enforced server-side.
   Do **not** carry `JoinTableRequest.SeatNumber` into PvP games — it's safe for house-banked
   blackjack only.
5. **Club rewards are non-cashable Chips/cosmetics only** — never the token, never a pooled
   contribution paid out on competitive outcome (that structurally resembles a wager pool).
6. **Club perks never affect odds or wagerable balance.**
7. **Legal note:** US courts in the social-casino MDL have reasoned that buying chips existing
   *"solely to gamble"* is itself a gambling transaction [DOC]. Tolerating a chip RMT market hands a
   plaintiff the argument that your chips have real cash value. **Anti-RMT is load-bearing for the
   CLAUDE.md rule-2 legal position, not just an integrity nicety.**

---

## 12. Definition of done
- Clubs are a social layer over public matchmaking; no private tables, no club economic control.
- Club XP accrues automatically from clean wagered play; Club Fund burns chips for system-minted perks.
- Level gates member slots + gift slots + cosmetics; total economic advantage capped ≤~20% of free-chip income.
- Roles + permission matrix + inactivity/succession cascade live; leave penalty vests rewards.
- Weekly co-op goal with per-member cap, low personal floor, everyone-who-participates payout.
- Per-member contribution visible in club UI.
- Club tag + emblem rendered at the table nameplate.
- Anti-abuse: excess-co-occurrence scoring, one-sidedness ratio, club centralisation ranking,
  registration clustering, device collision share, tenure/uniqueness eligibility gates, ranked
  review queue.
- Cold start staged (system clubs → recommended → player-created); 90/10 holdout instrumented.
- No peer chip transfer anywhere; `dotnet build` green; no NON-NEGOTIABLE weakened.

---

## Sources
GameRefinery *Casual Games are "Guilding Up"* + *Communal Mechanics* (guild adoption, casino lag,
Big Fish Clubs) · The Why Of Play social-casino retention teardown · Huuuge Help Center (club
creation 2M chips, League Points, Club Conquest, Jackpot Share, leave penalty) · Slotomania SlotoClans
(10-cap, Clan Key, clan albums) · Bingo Blitz Teams Treasure · Coin Master Teams · CoC Wiki (Clan
Perks, Clan Games caps, CWL structure, Clan Badge) · Supercell blogs (matchmaking, Clan Wars 2) ·
Ducheneaut et al. CHI 2007 (guild size/churn/survival) · Greige et al. arXiv 2203.05121 (collusion
detection, Isolation Forest) · UKGC RTS 11 (collusion signals) · IDnow chip-dumping · ProfessionalRakeBack
(Pokerrrr 2 / PokerBros / PPPoker reviews) · PokerBotAI (bots sold to club owners) · Ethan Levy,
Game Developer (guild-event whale spend) · Latané/Ringelmann social loafing · SCCG/ClassAction.org
(social-casino MDL).
