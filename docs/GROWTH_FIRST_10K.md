# Getting Khela's First 10,000 Downloads — Playbook

*A sequenced, Khela-specific growth plan (solo dev, small budget, Bengali/South-Asian, non-cashable
social casino, Android-first). Figures flagged [DOC] documented / [EST] estimate. Ad/store policies
change — verify against live policy pages at submission. Lawyer review of BD/IN framing per CLAUDE.md #4.*

---

## 0. The honest shape of this

10k purely organic takes **months, not weeks**, and ~80% of indie games don't break even in year one.
But Khela sits on three advantages competitors underuse, and the plan is built to exploit all three:
- **The world's cheapest CPI market is your exact target** (India Android ~$0.10–0.50, Bangladesh
  <$0.20 [EST]).
- **An under-localized store gap** — only ~3 of 10 top casual games even ship a Hindi listing; Bengali
  is wide open [DOC].
- **The best-retaining genre** — card/casino has the **longest sessions (~22 min, ~5× other genres)**
  and best D30 of any genre [DOC], plus **pre-existing cultural demand** (Teen Patti is a household game).

**Do not chase 10k directly.** 10k poured into an unproven loop is 10k churned users and a burned
budget. The path runs *through* proving retention at ~1k. Sequence below.

---

## 1. The gate nobody warns you about (do this FIRST)

**Google Play closed-testing requirement.** Personal developer accounts created after **Nov 13, 2023**
must run a **closed test with ≥12 testers opted in continuously for 14 days** before they can even
*apply* for production access [DOC] (lowered from 20→12 on Dec 11, 2024 — old blogs say 20). The 14
days must be consecutive; a tester who drops resets. **Recruit ~15–20 to reliably hold 12.**

- Track order: Internal test → **Closed test (the 12×14 gate)** → apply for production (≤7-day review)
  → Open test unlocks → Production. Open testing can't *satisfy* the gate.
- **Org (business-entity) accounts are exempt** — if you can register Khela as a company, you skip
  this gate entirely. Worth considering (also needed for iOS later — see §7).
- Satisfy it *and* soft-launch at once: recruit testers from **Bengali/SA Facebook card-game groups,
  WhatsApp/Telegram gaming groups, r/androiddev, r/betatesting, TestersCommunity** — on-target testers
  double as your first feedback cohort. Google's production questionnaire now scores tester engagement,
  so mix real target players with exchange testers (pure "12 ghosts" can fail the application).

This is your real day-one obstacle — not policy, not marketing. Plan a 2-week runway.

---

## 2. Prove the loop before spending a rupee on acquisition

Launch **blackjack first** (vs-dealer → a single player is a complete game → zero liquidity
requirement; acquire users one at a time and each has fun immediately). Soft-launch in-region (your
target *is* the cheap geo, so retention reads honestly — but South Asia has the lowest retention band
globally, so calibrate down).

**Gate before any paid UA** (re-acquiring a churned player costs 5–10×):

| Signal | Floor | Aim (genre lift) |
|---|---|---|
| **D1 retention** | ≥25% (below 25% → fix onboarding) | 32–38% [DOC social-casino top] |
| **D7 retention** | — | ~12–20% |
| **Payer conversion** ("do strangers pay") | ~2% | — |
| Session length | — | ~22 min is the genre norm |

**D1 < 15% = do NOT buy any traffic.** Until you can measure D1/D7/D30 + per-geo LTV, you can't know
your max profitable CPI, so paid UA is just burning cash. This *is* your Phase-0 gate.

---

## 3. Build the growth INTO the product (free, compounding — highest leverage)

A multiplayer social card game that isn't viral is leaving its cheapest channel on the table. K = i×c
(invites × conversion); K>1 = exponential. Build, in order:
1. **Invite-a-friend-for-chips, double-sided** (both get a stack), with the most generous version
   **gated behind "low balance"** — the HQ-Trivia trick: tie referral to a real game need. Referred
   users retain ~30% better [DOC].
2. **Gifting** chips to FB/WhatsApp contacts (Teen Patti's social DNA; already partly built).
3. **Share-to-social on big wins** — auto-generate an "I won X!" image card with a WhatsApp/FB share
   button. **Design every share WhatsApp-first** — that's the actual transmission medium here.
4. **Clubs/leagues** — private tables you invite friends/family to (huge cultural fit; Teen Patti is a
   group-of-friends game).
5. **Daily-bonus / streak / mission LiveOps** — the retention engine that makes word-of-mouth compound.
   This matters *more than ads* early; retention is the multiplier on every install.

Instrument i and c per channel; optimize toward K→1. (Keep bot-seated and gifted-chip activity out of
progression/leaderboards per the bot + two-bucket notes, so the loop can't be farmed.)

---

## 4. Channel stack for the first ~3k (free / near-free)

**Facebook is the home base — far more than in the West.** Bangladesh: ~45–60M FB users ≈ ~90% of all
social users; many treat "Facebook" as "the internet" [DOC]. Instagram/X never caught on for Bangla.
- Build a **FB Page + FB Group** as the community home (not IG, not X). Seed authentically into existing
  **Teen Patti / Ludo / regional-gaming Groups** (don't spam-drop links — Groups punish it).
- Secondary: **Telegram broadcast channel** (search-discoverable) + a small **Discord** for superfans/
  diaspora. Hybrid: broadcast on FB/Telegram, deep discussion on Discord.

**ASO — your cheapest standing channel.**
- Localize the listing into **Bengali + Hindi**, both **romanized and script** (Indians search "teen
  patti" romanized AND in Devanagari). Title (30 char) + short description (80 char) are the heaviest
  keyword fields. Target: teen patti, 3 patti, teen patti gold, andar bahar, rummy, blackjack, card game.
- **First 3 screenshots ≈ 80% of the install decision** [DOC]: frame 1 = hook, frame 2 = real
  gameplay (the social table, not just cards), frame 3 = social proof. Bold captions at the **top**,
  branded color background. Add a gameplay preview video (real gameplay in first 3 seconds).
- **Run Google Play Store Listing Experiments** (free A/B on real traffic) — test the **icon first**
  (5–15% swings), then the first 3 screenshots; one element at a time, ≥1 week each.

**Reviews:** below 3.5★ you rank for ~3× fewer keywords; 90% of featured apps are 4.0★+ [DOC]. Trigger
the **in-app review prompt after a positive moment** (a win / level-up / big chip pop), never after a
loss or during onboarding (max 3 prompts/365 days via the official API). Getting the first ~20–50
genuine ratings fast is a priority, not an afterthought.

**Short-form video (TikTok/Reels/Shorts):** no following needed — every clip gets a fresh algorithmic
shot. Show the actual game + a hook in 2 seconds ("What's your highest hand?"). 3–7 posts/week; treat
each as a lottery ticket (2025–26 organic reach tightened). *Organic only for casino content — paid
TikTok casino ads are effectively banned (§6).*

---

## 5. Creators + small paid to close the gap to 10k

**Micro/nano creators are the highest-ROI paid-ish lever, and culturally native.** South-Asian rates
are a fraction of Western:
- **Bangladesh micro (10K–100K): ~$40–80/piece** [EST]; FB micro-posts can be a few dollars.
- **India micro: ~$95–$1,800**, low end realistic for smaller creators [EST].
- A **$300–800 budget buys a *portfolio* of 10–20 micro creators** (spread risk) — far better than one
  mid-tier. Recruit creators already streaming Teen Patti / Ludo / rummy (pre-qualified audience).
- **Deal structure: hybrid** — small flat base + per-install affiliate (tracking link). Aligns
  incentives.
- **Creative that converts for card games: friends/family around a lively table** (Octro's winning
  format that drove 800% paying-user growth), big-win/bluff clips — sell the *social table*, not the cards.

**Small in-region paid top-up — only after §2 gate clears.**
- Channels: a single **Google App Campaign** in India/Bangladesh on **tCPI** for cheap install
  volume + retention data; layer **Meta** value campaigns for diaspora later.
- At ~$0.10–0.50 India CPI [EST], **$300–500 can plausibly buy several thousand in-region installs** —
  but these are **liquidity, not revenue** (low ARPU). Their job is to fill tables and surface whales.
- Min viable budget rule: daily budget ≈ 50× target CPI to let the campaign learn.

**Diaspora = where the money is (later).** US/UK/Gulf Bengali/Indian communities: CPI $2–12+ [EST/DOC]
but 10–40× the ARPU. Reach via **vernacular (Bengali) creators** and **expat FB/WhatsApp groups**
("Bangladeshis in London," Gulf-worker groups). Spend real UA money here *only after LTV is known*.
**Judge the portfolio on blended ROAS** — never hold cheap SA and pricey diaspora to the same CPI.

---

## 6. Ad-policy landmines (this is where solo devs get their accounts killed)

Your game is **social casino** (simulated, non-cashable) — a *restricted-but-allowed* category, NOT
real-money gambling. The whole strategy hinges on **never implying real money / cash-out / winnable
prizes** in any creative or listing.

- **Google Ads:** social-casino is **RESTRICTED — requires per-account, per-country certification**
  (apply with app ID; separate application per country). Allowlist **includes India, UK, US** but
  **NOT Bangladesh** → you can't run *certified Google social-casino ads* into BD (use ASO/organic/
  creators/rewarded there instead). Mandatory creative disclaimers: 18+, "not real-money gambling, no
  prizes of real value," disclose IAP, no real-casino brand logos. New (Mar 2026): account must show
  "good policy health" first. **Violations = account suspended on detection, no warning.**
- **Meta:** social-casino allowed **without prior authorization** (2025 update) if no real-money
  reward — but **18+ only, supported markets, disclaimers** required. Lowest-friction paid start for
  diaspora.
- **TikTok:** effectively **bans** casino-style paid ads — organic/non-gambling-framed only.
- **The token:** never advertise the Phase-2 token as redeemable/winnable — that risks reclassifying
  Khela as real-money gambling (the same change that killed sweepstakes social casinos in Oct/Nov 2025).
- **Don't misread "India excluded" headlines** — those refer to real-money gambling **ads/apps**, NOT
  play-money social casino. Play-money Teen Patti ships to India and Bangladesh storefronts today.
- **Rewarded/offerwall networks** (ironSource/Unity, AppLovin, Tapjoy) are often the most accessible
  paid-install source for a solo dev (less trigger-happy than search/social) — but confirm social-
  casino acceptance per network, and expect worse retention from rewarded installs.

**Store-review guardrails:** rate **adults-only / 17–18+**, **age-gate sign-up**, never enroll in
Designed-for-Families, and **disclaim "no real money / virtual chips only" in listing + in-app.** On
iOS later: Apple won't accept simulated-gambling apps from a **personal** account (needs an incorporated
entity) and Teen Patti/blackjack is a Guideline 4.3 "saturated category" rejection risk — your 3D table
+ social/LiveOps layer + original assets is the differentiation that clears it. **Android-first is correct.**

---

## 7. The sequence (one page)

1. **(Now)** Register dev account — consider an **org/company account** to skip the 12-tester gate and
   enable iOS later.
2. **Closed test:** recruit ~15–20 on-target testers from Bengali/SA FB/WhatsApp/Telegram + tester
   communities; hold 12 for 14 days; apply for production. (Doubles as soft-launch feedback.)
3. **Soft-launch blackjack** in-region; **build the viral loops + daily-bonus LiveOps**; instrument
   retention. **Gate: D1 ≥25–30%, ~2% pay.** Fix onboarding if below.
4. **Localize** Bengali/Hindi listing (romanized + script); optimize first-3 screenshots + icon via
   free Store Listing Experiments; seed first 20–50 reviews.
5. **Free channels** to first ~3k: FB Page+Group + seeding into Teen Patti groups, short-form video,
   Reddit, the in-product viral loops compounding.
6. **Creator portfolio** (10–20 micro, hybrid flat+affiliate, "social table" creative) + **small
   in-region paid** (Google App Campaign tCPI, India/BD) once the gate clears → push toward 10k.
7. **Apply for Google Play featuring + Indie Games Accelerator** once you're ~4★ with decent retention
   (featuring amplifies traction, doesn't create it; ~99% of featured apps are localized — you will be).
8. **Open the diaspora front** (vernacular creators + expat groups, Meta 18+) for revenue once LTV is
   proven. Launch **Teen Patti** once you have the concurrency to fill its tables (bots bridge the gap).

**Bottom line:** the cheat code is the overlap of cheapest-CPI-market + under-localized-Bengali-gap +
best-retention-genre + pre-existing Teen Patti demand. Tighten the funnel (ASO, reviews, soft-launch
discipline, viral loops), let retention + word-of-mouth do the heavy lifting, and use a few hundred
dollars of in-region paid + a featuring nomination to cross 10k. Retention is the flywheel; ads are
the top-up — never the other way around.

---

## Sources (key)
- Google Play testing requirement (12 testers/14 days): support.google.com/googleplay/android-developer/answer/14151465
- Google Play gambling/social-casino policy + violations: support.google.com/googleplay/android-developer/answer/9877032 ; .../answer/13381106
- Google Ads social-casino certification + allowlist (incl. India/UK/US, not BD): support.google.com/adspolicy/answer/15132179
- Meta social-casino (no pre-auth, 18+, disclaimers): transparency.meta.com/policies/ad-standards/restricted-goods-services/gambling-games/
- TikTok gambling ad ban: ads.tiktok.com/help/article/tiktok-ads-policy-gambling-and-games
- CPI benchmarks: businessofapps.com/ads/cpi/research/cost-per-install/ ; Liftoff 2025 Casual Gaming Apps Report ; mapendo.co/blog/cost-per-install-by-country-2025 ; AppsFlyer Performance Index
- Retention/soft-launch benchmarks: gameanalytics.com/reports/2025-mobile-gaming-benchmarks ; a16z.com/mobile-game-soft-launch/
- ASO/localization (India/Bengali gap, first-3-screenshots, Store Listing Experiments): apptweak.com/en/aso-blog/how-to-localize-your-app-in-india ; play.google.com/console/about/store-listing-experiments/
- Creator costs SA: upgrowth.in/influencer-marketing-pricing-india-2026/ ; agentwisex.com/influencer-marketing-bangladesh/
- FB dominance BD: datareportal.com/reports/digital-2025-bangladesh ; thedailystar.net/tech-startup/news/social-media-use-bangladesh-grows-223-2024-facebook-leads-3735526
- Referral/K-factor + social-casino loops: viral-loops.com/blog/referral-program-for-games/ ; gameanalytics.com/blog/referral-marketing-mobile-games
- Teen Patti growth case + whale economics: prnewswire.com (Octro 800%) ; icon-era.com/blog/how-social-casino-monetisation-actually-works.561/
