# Leaderboards + Per-Game Stats

*Owner: Reza · Last updated: 2026-06-27*

Rebuilt to **plain SQL** (June 2026). The earlier Redis-ZSET / seal-job / instance / archive design was more than
this game needs at its scale — the model below is "upsert latest values + query by date range," which is all a
leaderboard is until you have millions of rows + hot read traffic.

## Principle

Two layers, two time-scopes:
- **All-time** — running totals, already maintained every round. Per-game = `UserGameStats`; cross-game = `UserProfile`.
- **Windowed** (daily/weekly/monthly) — a date-range query over a small daily rollup, `PlayerDailyStat`.

**Overall board = XP only.** XP is the one comparable, farm-proof, unbuyable metric. Never an overall board on
chip balance (whale board) or net profit (negative-sum, collusion-farmable). Per-game boards use skill/aspirational
metrics (XP, biggest win, longest streak).

## Data model (what each table stores)

| Table | Scope | Key | Holds |
|---|---|---|---|
| `UserGameStats` | per-game **all-time** | (UserId, GameType) | XP, ChipsWon, TotalWagered, NetProfit, BiggestSingleWin, GamesPlayed/Won, RoundsPlayed/Won, Current/LongestWinStreak, Region, First/LastPlayedAt |
| `UserProfile` | cross-game **all-time** | UserId | LifetimeExperience + aggregate counts/streaks/wagered/won/biggestwin |
| `PlayerDailyStat` | **windowed** | (UserId, GameType, StatDate) | per-UTC-day: Xp/GamesPlayed/GamesWon/Wagered/ChipsWon/NetProfit (summed) + BiggestSingleWin (daily MAX) + Region. **No streak** (can't range-aggregate). Pruned 90d. |

All three are written on settle in `PlayerStatsService.RecordRoundResultsAsync` (same per-round values; one extra
upsert for `PlayerDailyStat`). The daily rollup is the ONLY new table; all-time is free (already maintained).

## Boards

- **Overall:** `xp` (weekly + all-time).
- **Per-game** (per available game): `xp`, `biggestwin`, `streak`. **Streak is all-time only** (a streak crosses day
  boundaries; can't be summed from daily buckets).
- Metrics: `xp`/`gamesPlayed`/etc. = SUM over the range; `biggestwin` = MAX over the range; streak = MAX over all-time.

## Read API (`Khela.Game` → `LeaderboardController`)

```
GET /api/leaderboard/boards
GET /api/leaderboard?game={general|blackjack|…}&metric={xp|biggestwin|streak}
                    &period={daily|weekly|monthly|alltime}&scope={global|friends|country}&top=50
  → { entries:[{rank,userId,displayName,avatarId,region,score}], me:{rank,score,…} }
```
- **All-time** → `ORDER BY` over `UserGameStats` (per-game) / `UserProfile` (overall). **Windowed** → date-range
  `GROUP BY UserId SUM/MAX` over `PlayerDailyStat`.
- **Scope:** Global = no filter; **Friends** = `UserId IN (accepted friend ids)`; **Country** = `Region = caller's`.
- **Self-rank** = count-of-players-above + 1 (returned even when outside top-N).
- Clean-earned metrics; bot tables excluded; block-aware; moderated displayName.

## Per-game profile stats (the profile tabs)

`GET /api/profile/me` and `/{userId}` return, alongside the existing aggregate:
- `stats` — the **All** tab (cross-game aggregate), now incl. `totalWagered`, `lastPlayedAt`, `startedPlayingAt`.
- `perGame[]` — one `GameStatsDto` per game played, newest-first: `{ game, displayName, gamesPlayed, gamesWon,
  winRate, totalWagered, biggestWin, netProfit, currentWinStreak, longestWinStreak, experienceEarned, lastPlayedAt,
  startedPlayingAt }`. Field names align with `stats` so one UI stat-block binds to any tab.
- WinRate = `GamesWon/GamesPlayed` (%); null when no games (or Slots). `NetProfit` own-profile only (null on public).

UI mapping (profile tabs): GAME PLAYED→`gamesPlayed`, WAGERED→`totalWagered`, BIGGEST WIN→`biggestWin`,
LAST PLAYED→`lastPlayedAt` (relative), STARTED PLAYING→`startedPlayingAt` (`dd/MM/yyyy`). Tabs = `All(stats)` + `perGame[]`.

## Verification

`AddPlayerDailyStats` migration applied; `dotnet test` 44/44 (settle upsert intact); all leaderboard query paths +
the per-game profile endpoints smoke-verified against the running backend (real data — e.g. a player with 588
Blackjack rounds, 5.07M wagered, 25k biggest win, 28.6% win-rate, NetProfit hidden on the public view).

## TODO / roadmap

- [ ] **Client leaderboard screen** — board picker × period × scope + pinned self-rank (consumes `/api/leaderboard`).
- [ ] **Client profile per-game tabs** — scrollable tab bar (All + per game) binding `stats`/`perGame[]`.
- [ ] **Dashboard Leaderboards module** (`Khela.Web`) — admin view over the same endpoints.
- [ ] **Remove the dead Redis push** in `PlayerStatsService` (`LeaderboardService.RecordRoundAsync` still fires but
      nothing reads it) + retire the unused `LeaderboardService`/instance/archive/Season code.
- [ ] **Future games** — when poker/teenpatti/etc. ship: their settle calls `RecordRoundResultsAsync` with the
      mapped leaderboard `GameType` (Holdem/Omaha → Poker; **map, never cast** — two GameType enums diverge), and
      their boards appear automatically (per-game boards are data-driven off `UserGameStats`/`PlayerDailyStat`).
- [ ] **Rewards** (optional) — if weekly prizes are wanted, add a period-rollover snapshot→reward pass (deliberately
      omitted; the simple model has no seal job).
