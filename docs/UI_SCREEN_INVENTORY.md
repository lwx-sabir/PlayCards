# UI Screen & Popup Inventory — Pre-Release

*Complete surface inventory for Khela World v1. Priority: **P0** = launch blocker (legal, core loop,
error handling) · **P1** = needed at launch for retention/monetization · **P2** = post-launch.
Roughly 70 distinct surfaces. The ones that sink launches are §1 (legal), §9 (errors) and §10
(empty states) — not the fun screens.*

---

## 0. The three rules that generate half this list

1. **Every screen needs three variants:** loading, empty, and error. A leaderboard with no data, an
   inbox with no messages, a friends list with no friends — if you don't design these, they ship as
   blank rectangles.
2. **Every destructive or money action needs a confirm** — and every confirm needs a success *and* a
   failure state.
3. **Legal screens are launch blockers, not polish.** Missing an age gate or a delete-account flow
   fails review; you don't get to ship and add it later.

---

## 1. Legal & compliance (P0 — these block store approval)

| Screen | Notes |
|---|---|
| **Age gate** | Before any gameplay. 18+ (or local age). Store the result. Social-casino requirement. |
| **ToS + Privacy Policy acceptance** | First launch. Must be re-shown on material change. Links must work. |
| **"Not real gambling" disclaimer** | The three standard social-casino disclaimers: *adult audience · no real money or prizes of value · success here doesn't imply success at real-money gambling.* Needed in-app **and** in the store listing. |
| **Subscription terms sheet** (Golden Pass) | Price, period, **auto-renew disclosure**, cancel instructions, restore. Apple is strict; this fails review often. |
| **Delete account flow** | GDPR/CCPA requirement — and Apple requires in-app account deletion for any app with account creation. Confirm → grace period → confirmation. |
| **Privacy / data consent** | Analytics + ads consent (GDPR regions), ATT pre-prompt on iOS. |
| **Responsible play panel** | Session-time reminder, self-exclusion / cool-off toggle, links to help. Pairs with the marketing-spec suppression flag. |
| **Region-blocked screen** | For any geo you exclude. |

---

## 2. Boot & entry (P0)

- **Splash** (logo, 1–2s max)
- **Loading screen** — progress bar + rotating tips. Never a frozen logo.
- **Auth / guest sign-in** (device-guest is silent; needs a fallback UI when it fails)
- **Account-link prompt** — "Link to save your progress." Critical: device-guest players lose everything on device change. Nag gently, repeatedly.
- **FTUE / tutorial** — the biggest funnel leak (only ~30% of players complete tutorials). Keep it <30s, interactive, skippable-after-first-hand.
- **Force-update popup** — blocking, with store link. You will need this.
- **Maintenance mode screen** — server down, with ETA if known.

---

## 3. Home & navigation (P0)

- **Home** (per `UI_HOME_SPEC.md`)
- **Settings** — root
  - Audio (music / SFX / vibration, separate sliders)
  - Graphics quality (Low/Med/High/Auto per the tier plan)
  - Language
  - Notifications
  - Account (link, logout, **delete**)
  - Privacy & consent
  - Support / contact
  - Legal (ToS, Privacy, disclaimers)
  - Version + build info (needed for support tickets)
- **Own profile** — avatar, name, level/XP, aggregate stats + **per-game breakdown**, edit name/avatar
- **Other player profile** — public, block-aware, with **Report** and **Block**
- **Avatar / cosmetics picker** (P1 if cosmetics ship at launch)

---

## 4. Core game loop (P0)

- **Lobby / table browser** — filters by stake, seat availability, game
- **Table screen** — the game itself
- **Bet UI** — chip selection, repeat bet, clear, all-in guard
- **Action bar** — hit/stand/double/split (per game)
- **Round result banner** — win/lose/push/blackjack + delta
- **Hand history / last hand** (P1, but ties to provably-fair trust)
- **Provably-fair verify screen** (P1) — your differentiator; surface it
- **Leave table confirm** — "You have chips in play" warning
- **Seat selection** (if used)
- **Insufficient funds popup** ← **the single most important monetization surface in the game.** Fires when balance < min bet. Offers: buy chips / free chips (rewarded ad) / lower-stake table. Do not make this a dead end.

---

## 5. Economy & monetization (P0/P1)

- **Shop** — chip packs, Kash packs, offers. Featured/limited items on top.
- **Purchase confirm** → **success** (with coin-fly) → **failure** (payment declined, network, cancelled)
- **Restore purchases** (P0 — store requirement)
- **Golden Pass screen** — free vs paid track, daily claim, days remaining
- **Subscription manage / cancel** (deep link to store)
- **Exchange screen** (Chips → Kash) + **confirm** ("This costs 1,000,000,000 chips — are you sure?")
- **Daily bonus / chest claim** — big, celebratory, with streak state
- **Rewarded-ad flow** — offer → ad → reward granted → failure (no fill)
- **Offer popup** (targeted, from the marketing engine) — with a clear close button

---

## 6. Progression (P1)

- **Level-up celebration** — full-screen moment, chips fly, sound sting
- **VIP / tier screen** — current tier, progress, perk table, next-tier preview
- **Quests / missions** — daily + weekly, claim states
- **Rewards centre** — everything claimable in one place
- **Loyalty store** (P2)
- **Achievements** (P2)

---

## 7. Social (P1)

- **Friends list** — online status, invite, gift
- **Add friend / search** — by name or code
- **Friend requests** — incoming/outgoing
- **Gift send / receive** — fixed-denomination, rate-limited
- **Chat** — table chat + DMs, with **report/block/mute per message**
- **Quick-chat / emote picker** (fixed IDs, no free text — minor-safe)
- **Report player / report message** — with reason picker (launch blocker for Apple 1.2 UGC if you have chat)
- **Inbox** — system messages, rewards, announcements
- **Leaderboards** — board picker × period (weekly/all-time) × scope (global/friends/country), with the caller's own rank always pinned
- **Club/clan screens** (P2 — see `CLUB_SPEC.md`)

---

## 8. Notifications & permissions (P0)

- **Push permission pre-prompt** — soft ask *after a positive moment* (first win / first bonus claim). Never fire the OS prompt cold.
- **Notification settings** — per-category toggles
- **ATT pre-prompt** (iOS)

---

## 9. Error & connection states (P0 — most commonly missed)

Every one of these needs a real screen, not a silent failure:

- **No internet** — with retry
- **Connection lost mid-hand** — reconnecting spinner + what happens to my bet
- **Reconnect success / failed**
- **Session expired** — re-auth silently if possible, else prompt
- **Server error (5xx)** — generic, with a support code
- **Kicked from table** (stalled/reaped) — explain why
- **Table full / no longer available**
- **Purchase failed** — distinct messages for declined / cancelled / network
- **Insufficient funds** (also §4 — it's both)
- **Wallet locked** — rare, but you have the state
- **Action rejected by server** — invalid move, out of turn
- **Rate-limited** — "slow down"
- **Version mismatch** — force update

> The APK table bug you hit earlier was exactly this category: a connection failure with no visible
> error state, so it presented as a frozen camera. **Silent failure is the worst failure.**

---

## 10. Empty states (P0 — cheap, and their absence looks broken)

- Friends list (no friends) → "Invite a friend" CTA
- Inbox (no messages)
- Inventory (no items)
- Quests (all complete)
- Rewards (nothing to claim)
- Leaderboard (not enough data / not ranked yet)
- Lobby (no tables at this stake) → suggest another stake
- Chat (no messages)
- Purchase history (none)

Each should have art + one line + an action. Never a blank panel.

---

## 11. Loading & transition states (P0)

- Scene-transition loader (Home → Table)
- Inline spinners on any network call >300ms
- **Skeleton screens** for lists (leaderboard, friends, shop) — better perceived speed than spinners
- Optimistic UI where safe (never for money)

---

## 12. Live-ops surfaces (P1)

- **Event banner** (home slot)
- **Event details screen**
- **Tournament screen** — leaderboard, timer, entry, prizes
- **Season/pass progress**
- **Announcement / news popup** — max once per session, dismissible

---

## Priority summary

**P0 — cannot ship without:** all of §1 legal · boot flow · settings (incl. delete account) · core
game loop + insufficient funds · shop + purchase states + restore · all of §9 errors · all of §10
empty states · loading states · push pre-prompt · report/block if chat is live.

**P1 — ship-with:** progression screens · social · daily bonus · Golden Pass · leaderboards ·
event surfaces · avatar/cosmetics.

**P2 — after:** clubs · loyalty store · achievements · hunting · virtual world.

---

## Cross-cutting checklist (applies to every surface)

- **Close button on every popup**, top-right, always in the same place, always ≥44pt
- **Back button / Android hardware back** handled on every screen
- **Safe areas** — notches, rounded corners, gesture bars
- **Aspect range** — 16:9 → 20:9 → ultrawide → resizable browser window
- **Text overflow** — long names, big numbers, other languages (German/Bengali run long)
- **Localization-ready strings** — no baked text in images
- **One modal at a time** — a queue, not a stack (ties to the home slot arbiter)
- **Consistent popup anatomy** — same header, same close, same button order, same animation
- **Sound on every interaction**
- **Debug overlays stripped from release builds**
