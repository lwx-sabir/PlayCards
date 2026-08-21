# Khela World — Documentation Index

*Read this first. It maps every doc to **when** it's relevant, so you (or the build agent) load the
right two files instead of guessing across 25.*

**Start here, always:** [`../CLAUDE.md`](../CLAUDE.md) (NON-NEGOTIABLE rules + current state) →
[`DECISIONS.md`](DECISIONS.md) (why things are the way they are) → the one spec for the task at hand.

---

## Core — read before any work

| Doc | What it's for |
|---|---|
| [`../CLAUDE.md`](../CLAUDE.md) | The constitution: NON-NEGOTIABLE rules, architecture, conventions, current state. **Keep it current — a stale CLAUDE.md actively misleads the agent every session.** |
| [`DECISIONS.md`](DECISIONS.md) | Append-only decision log with rationale. Check here before re-opening a settled question. |
| [`PROJECT_PLAN.md`](PROJECT_PLAN.md) | Master plan: vision, phase sequencing, economy, legal posture, roadmap. |
| [`PROJECT_REVIEW_2026-06-25.md`](PROJECT_REVIEW_2026-06-25.md) | Whole-project audit — strengths, blockers, prioritised fix order. |

## Games — one spec per game

| Doc | Status | Notes |
|---|---|---|
| [`TEEN_PATTI_SPEC.md`](TEEN_PATTI_SPEC.md) | Not built | PvP pot game — pot escrow, blind/seen, side-show. First PvP engine. |
| [`THREE_CARD_POKER_SPEC.md`](THREE_CARD_POKER_SPEC.md) | Not built | Vs-dealer; reuses blackjack. Can dual-skin as house-banked "Teen Patti". |
| [`VIDEO_POKER_SPEC.md`](VIDEO_POKER_SPEC.md) | Not built | Slots-family (single-player vs paytable) + social wrapper. |
| [`TEXAS_HOLDEM_SPEC.md`](TEXAS_HOLDEM_SPEC.md) | **Build LATE** | Side pots, biggest collusion surface, worst liquidity. Reads the pot-escrow work from Teen Patti. |

*(Blackjack has no spec doc — it's built; rules live in the engine + `CLAUDE.md`.)*

## Systems

| Doc | What it covers |
|---|---|
| [`PROGRESSION_SPEC.md`](PROGRESSION_SPEC.md) | XP/Level, VIP/Status, Loyalty **and** the two-bucket clean/tainted wallet (§6). Money-path adjacent. |
| [`PASS_SPEC.md`](PASS_SPEC.md) | Golden Pass monthly subscription + daily collect. |
| [`LEADERBOARD.md`](LEADERBOARD.md) | Board matrix, periods, scopes, per-game vs cross-game stats. |
| [`AVATAR_SHOP_SPEC.md`](AVATAR_SHOP_SPEC.md) | Cosmetics / avatar store. |
| [`CLUB_SPEC.md`](CLUB_SPEC.md) | Clubs as a social layer over public matchmaking; club abuse/collusion guardrails. |
| [`NETWORKING_SPEC.md`](NETWORKING_SPEC.md) | The two planes (ASP.NET tables vs FishNet world), hunting tournament, money boundary. |
| [`GRAPHICS_QUALITY.md`](GRAPHICS_QUALITY.md) | URP quality tiers, per-platform defaults, mobile dials. |

## Social & moderation

| Doc | What it covers |
|---|---|
| [`PROFILE_SOCIAL_SPEC.md`](PROFILE_SOCIAL_SPEC.md) | ProfileController, bio/status, LastSeenAt, block-aware reads, report model. |
| [`CHAT_SPEC.md`](CHAT_SPEC.md) | Chat moderation (rule-based, multi-language), room-chat persistence, quick-chat emotes. |
| [`SOCIAL_BACKLOG.md`](SOCIAL_BACKLOG.md) | Deferred social items (achievements, gift variety, AI moderator, follow/mute). |

## UI

| Doc | What it covers |
|---|---|
| [`UI_HOME_SPEC.md`](UI_HOME_SPEC.md) | Home zone map, three visual tiers, progressive disclosure, badge rules, juice/animation checklist. |
| [`UI_SCREEN_INVENTORY.md`](UI_SCREEN_INVENTORY.md) | **Every screen/popup needed before release**, P0/P1/P2. Legal screens, error states, empty states. |

## Growth & operations

| Doc | What it covers |
|---|---|
| [`MARKETING_SPEC.md`](MARKETING_SPEC.md) | Funnel, build-vs-buy stack, the in-house marketing engine (KME), campaign catalogue, event taxonomy. |
| [`GROWTH_FIRST_10K.md`](GROWTH_FIRST_10K.md) | Launch playbook: the Play closed-test gate, retention gates, channels, CPI, ad policy. |
| [`NAMING_V1.md`](NAMING_V1.md) | Brand + store-title strategy, the `khela.app` betting-brand collision, clearance checklist. |
| [`ADMIN_DASHBOARD.md`](ADMIN_DASHBOARD.md) | Admin/ops tooling. |

## Reference / historical

| Doc | What it covers |
|---|---|
| [`DB_AUDIT_2026-06-19.md`](DB_AUDIT_2026-06-19.md) | Money-path DB audit (wallet ledger invariants). |
| [`INVECTOR_INPUT_MIGRATION.md`](INVECTOR_INPUT_MIGRATION.md) | Invector controller input migration notes. |

---

## Which docs to load for a given task

| Working on… | Load |
|---|---|
| Anything at all | `CLAUDE.md` + `DECISIONS.md` |
| A new game | that game's spec + `PROGRESSION_SPEC` §6 (wallet buckets) |
| Anything touching money | `CLAUDE.md` rules 2–4 + `PROGRESSION_SPEC` §6 + `PROJECT_REVIEW` |
| The walkable world / hunting | `NETWORKING_SPEC` + `GRAPHICS_QUALITY` |
| UI work | `UI_HOME_SPEC` + `UI_SCREEN_INVENTORY` |
| Growth / LiveOps | `MARKETING_SPEC` + `GROWTH_FIRST_10K` |
| Clubs or leaderboards | `CLUB_SPEC` + `LEADERBOARD` |

---

## Conventions for these docs

- Specs are **written before implementation** and handed to the build agent.
- Every spec ends with a **Definition of Done**. That section should become **tests**, not just prose
  — an executable contract can't be falsely reported as complete.
- When a spec is implemented, **update `CLAUDE.md` current-state** and mark status here.
- New load-bearing decision → **append to `DECISIONS.md`** (never edit old entries; supersede them).
