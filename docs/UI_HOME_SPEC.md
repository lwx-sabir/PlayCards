# Home Screen — UI/UX Spec

*Khela World home screen (landscape, mobile-first, also PC/web). The information architecture in the
current build is already correct — this spec is about **visual hierarchy, progressive disclosure, and
juice**. Target: `Khela.Play` / `Assets/1Khela/Prefabs/Home`.*

---

## 0. The one principle

**Everything currently has equal visual weight, so nothing leads.** ~18 tappable elements at similar
size and saturation means a new player has no idea where to look. Successful social-casino homes
(Huuuge, Zynga Poker, Jackpot Party, Slotomania) are aggressively hierarchical: **one element
dominates, everything else recedes until needed.**

The fix is not a redesign. It's re-weighting, hiding, and adding motion.

---

## 1. Zone map (landscape)

```
┌──────────────────────────────────────────────────────────────────┐
│ [IDENTITY]          [GAME BRAND]              [WEALTH]    [⚙]   │
│ avatar·name·lvl·XP   (changes w/ carousel)   chips + Kash + "+"  │
├──────────┬────────────────────────────────────────┬──────────────┤
│ PROGRESS │           ┌─ EVENT BANNER ─┐           │ META/SOCIAL  │
│ season   │                                        │ ranking      │
│ pass     │      ★★★ HERO: TABLE CAROUSEL ★★★     │ quests       │
│ piggy    │        (swipeable, ambient life)       │ friends      │
│ bank     │                                        │ inbox        │
│          │        ┌──────────────────┐            │              │
│          │        │   ▶ PLAY NOW ◀   │  ← TIER 1  │              │
│          │        └──────────────────┘            │              │
│          │             lobby ›  (quiet)           │              │
├──────────┴────────────────────────────────────────┴──────────────┤
│ [SHOP] [EXCHANGE] [INVENTORY] [CLAN]              [DAILY CHEST]  │
└──────────────────────────────────────────────────────────────────┘
```

**Zone purposes** — one job each, never mix:
- **Identity (top-left):** who am I, how far to next level. Tap → profile.
- **Wealth (top-right):** what I have + how to get more. Highest-value monetization real estate.
- **Game brand (top-center):** which game the carousel is on. Must change as you swipe.
- **Progress rail (left):** *my* time-limited progression — season pass, piggy bank.
- **Meta rail (right):** social + systems — ranking, quests, friends, inbox.
- **Hero (center):** the game picker. The visual anchor.
- **Primary CTA (center-bottom):** PLAY NOW. The only Tier-1 element on screen.
- **Nav bar (bottom-left):** persistent destinations — shop, exchange, inventory, clan.
- **Daily free (bottom-right):** the chest + timer. The return hook.

---

## 2. Three tiers of visual weight (the core fix)

| Tier | Elements | Treatment |
|---|---|---|
| **1 — Dominant** (exactly one) | **PLAY NOW** | ~2× current size. Full saturation. Outer glow. Drop shadow. Idle pulse (§5). **Nothing may touch or overlap it** — minimum 40 px clear space on all sides. |
| **2 — Primary** | Wealth, hero carousel, daily chest, event banner | 100% opacity, full saturation. No idle motion **except** the chest when ready. |
| **3 — Ambient** | Left rail, right rail, bottom nav, game brand | **85% opacity, icon size −15%, saturation −20%.** Findable, not shouting. Full opacity on hover/press only. |

**Immediate consequences for the current build:**
- The **Golden Pass panel currently outweighs PLAY NOW** — that's inverted. Move it fully into the left rail at Tier 3.
- **LOBBY becomes a small text link** ("lobby ›") beneath PLAY NOW, not a peer button.
- Clear the overlap between the season-pass panel, LOBBY, and the table.

---

## 3. Element inventory + progressive disclosure

A level-1 player should see **6 elements, not 18.** Reveal the rest as they level — each unlock is a
small dopamine beat *and* it keeps the first session legible.

| Element | Visible from | Notes |
|---|---|---|
| Avatar / name / level / XP | L1 | XP bar must be legible — it's a free retention nudge |
| Chips + `+` | L1 | |
| Hero carousel | L1 | Locked games show as "coming soon" (aspiration) |
| **PLAY NOW** | L1 | |
| Daily chest | L1 | The return hook — must be obvious on day 1 |
| Settings | L1 | |
| Shop | L2 | |
| Quests | L3 | |
| Lobby link | L4 | Only once they understand "play" |
| Ranking | L5 | Meaningless before they have stats |
| Friends | L5 | |
| Inbox | L6 | |
| Season Pass | L6 | |
| Inventory | L8 | Needs items to hold |
| Exchange | L10 | Chips→Kash is a late-game concept |
| **Kash currency** | first Kash earned/bought | **Never show a dead `0`** |
| Clan | L12 | Show locked with "Unlocks at Level 12" on tap |
| Event banner | when an event is live | Hidden otherwise — no empty slot |

**Rule:** if a feature can't be used yet, either hide it or show it **locked with the unlock
condition stated on tap**. Never show an enabled control that does nothing.

---

## 4. Badge discipline

Current state has badges on Rewards, Quests, Inventory, and chat (99+) → **badge blindness.**

**A badge means: "there is a free thing you can claim RIGHT NOW."**

- ✅ Daily chest ready · unclaimed reward · completed quest ready to collect · unread *personal* message
- ❌ Quest in progress · new content available · unread broadcast notices · season-pass progress

**Max 2 badges visible at once.** If more qualify, show the two highest priority:
`daily chest > claimable reward > completed quest > personal message`.
Chat should show a dot, not "99+" — a 3-digit counter reads as spam.

---

## 5. The joy layer (this is what separates good from great)

### 5.1 Ambient life — the hero must never be static
- **Light sweep** across the felt: 8 s loop, subtle.
- **Card riffle / chip stack** idle animation every 8–12 s, randomized.
- **Slow camera drift** on the table (±2° over 20 s) — makes it feel like a place, not a screenshot.
- Cost is near-zero; impact is the single biggest "is this game alive" signal.

### 5.2 Entry choreography (on Home load)
Cascade over **~600 ms**, never all-at-once:
1. Table fades/scales in (0 ms, 300 ms, ease-out)
2. PLAY NOW pops (200 ms, 250 ms, **ease-out-back** — slight overshoot)
3. Wealth + identity slide from top (280 ms, stagger 60 ms)
4. Rails slide from edges (380 ms, stagger 60 ms)
5. Nav bar rises (460 ms)

This one change makes a screen feel expensive.

### 5.3 Tap juice — every interactive element, no exceptions
- **Scale punch:** 1.0 → 1.08 → 1.0 over **120 ms** (ease-out then ease-in)
- **Particle burst** on primary actions (6–10 small sparks)
- **Distinct sound** per element class (§6)
- Press-down state at 0.96 scale so touch feels physical

### 5.4 Coin-fly (the most satisfying animation in the genre)
On any chip grant: **8–12 physical chips arc** from the source to the wealth counter over
**400–600 ms** (staggered ~30 ms each, bezier arc), then the **counter rolls up** over 800 ms with a
ticking sound — never snap the number.

### 5.5 Anticipation states
- **Chest ready:** pulse 1.0 → 1.06 on a 1.2 s loop + warm glow + occasional shake.
- **Chest on cooldown:** desaturated, 70% opacity, timer visible.
- The difference between ready and not-ready should be **dramatic** — that contrast is what drives the daily return.

### 5.6 Level-up
Full-screen moment: dim background, badge scale-in with a burst, chips fly to the counter, sound
sting. It's a rare event — spend the 2 seconds on it.

---

## 6. Sound (half of "joyful", routinely skipped)

- **Ambient casino room tone** under everything, low volume, looping. A silent home feels dead
  regardless of art quality.
- **UI tap:** soft click. **PLAY NOW:** deeper, heavier "thunk" — it should sound more important.
- **Chips:** clink cascade on coin-fly. **Chest open:** short fanfare.
- **Carousel swipe:** a whoosh + soft detent on snap.
- Respect the device mute switch; provide a settings toggle.

---

## 7. Carousel affordance

Currently reads as one table with a fragment of another — players may not know it's a picker, which
makes your entire multi-game platform invisible.

- **Peek** the neighbouring tables at ~25% width, dimmed to 50% and scaled to 0.85.
- **Left/right chevrons**, subtly animated on first visit.
- **Position dots** beneath the table.
- **Game brand text (top-center) updates** as you swipe — reinforces that this is a selection.
- Snap with a soft detent + sound.

---

## 8. Fixes to the current build, ranked

1. **Set the three tiers.** Make PLAY NOW dominant; clear its space; demote LOBBY to a text link; move the season-pass panel into the left rail.
2. **Kill the dead `0`.** Hide Kash until the player has some.
3. **Badge cull.** Max 2, claimable-only.
4. **Remove/reskin the bottom-right character card.** The military/sniper card with SS rank + 198.5 is off-theme and is currently the most confusing element on screen. Either reskin to the BoZo avatar/cosmetic identity or cut it.
5. **Carousel affordance** (peek + chevrons + dots).
6. **Ambient motion + entry cascade + tap juice + coin-fly.**
7. **Add the event banner slot** — you have a full LiveOps/marketing plan and currently nowhere on Home to promote it.
8. **Progressive disclosure pass** — implement the level gates in §3.
9. **Social proof** (optional, cheap): "12,483 playing now" or a friend-activity ticker. Makes the game feel populated and pairs with bot seeding.

---

## 9. Definition of done

- Exactly one Tier-1 element (PLAY NOW), with enforced clear space; tiers 2/3 applied per §2.
- Level-gated visibility implemented; no enabled-but-dead controls; Kash hidden at 0.
- Badges are claimable-only, max 2, priority-ordered.
- Carousel reads unmistakably as a picker (peek + chevrons + dots + brand text sync).
- Ambient table motion, entry cascade, tap juice, coin-fly, chest anticipation all live with the
  timings in §5.
- Sound bed + per-class SFX wired, with a settings toggle and mute-switch respect.
- Event banner slot exists and hides cleanly when no event is live.
- Verified at 16:9 through ultrawide and in a resized browser window; safe areas respected;
  debug overlay stripped from release builds.
```
