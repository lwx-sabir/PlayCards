# Khela Admin Dashboard (`Khela.Web`)

*Owner: Reza · Last updated: 2026-06-26*

A dark-themed **ASP.NET Core MVC admin/ops console** for the Khela casino. Separate app from the
game backend, but shares the same MySQL + Redis. Ops tooling — **not** gameplay. Read-mostly; the one
exception is the live-tunable **Settings** (config overrides, never money).

- **Project:** `khela/Khela.Game/Khela.Web` (net8.0), in `Khela.Game.sln`.
- **References:** `Khela.Common` (DTOs) + `Khela.Game` (`AppDbContext`, models, `ProgressionService`).
- **Run:** `dotnet run --project khela/Khela.Game/Khela.Web` → open the printed URL.

---

## Auth

Cookie-based ASP.NET Identity reusing the game's `ApplicationUser` store (NOT JWT — this is server-rendered).
The **whole site** is admin-only via a fallback `Admin` policy = authenticated **AND** (Development is open,
OR the user's `AspNetUsers.Id` is in `Admin:UserIds`). Only `/Account/{Login,Register,Denied}` and the error
page are `[AllowAnonymous]`.

- **Dev login (created during testing):** `admin@khela.local` / `Khela123`.
- **Prod:** add admin `AspNetUsers.Id` GUIDs to `Admin:UserIds` in `appsettings.json`.
- `Register` creates an account (with `CountryCode = "bd"`, which is NOT-NULL) and auto-signs-in.

---

## Modules

| Module | Status | Backing |
|---|---|---|
| **Dashboard** (`/`) | ✅ live | `UserStatsController`, `GameStatsController`, `AlertsController` + `dashboard.js` |
| **Settings** (`/Settings`) | ✅ live | `SettingsController` → Redis `khela:settings` hash (engine overlays it) |
| **Wallets & Ledger** (`/Wallets`) | ✅ live | `WalletsController` read-only over `PlayerWallets` + `WalletTransactions` |
| **Players** | ⬜ placeholder | — |
| **Reports** | ⬜ placeholder | `Reports` table (alert count is already live) |
| **Leaderboards** | ⬜ placeholder | Redis live boards + MySQL seasons |
| **Game Tables** | ⬜ placeholder | Redis table state |

Placeholder nav items route to `Home/Soon?section=…` ("Coming soon").

### Dashboard
Four stat cards (Total Players, Chips Wagered 24h, Rounds 24h, Chips in Play), a 30-day wagered chart,
a Live System Alerts panel, and a Recent Players table — all real, fetched on load by `wwwroot/js/dashboard.js`
from the read-only JSON APIs under `Controllers/Api/`:
- `GET /api/stats/users` (+`/recent`, which filters out `smk*` smoke accounts), `GameStats`
  `GET /api/stats/games` (+`/wagered-series` = 30-day zero-filled), `GET /api/stats/alerts`
  (failed/stuck `WalletTransactions` + open `Reports` + Redis `IsConnected` → crit/warn/ok).
- All admin-gated, computed off `AppDbContext`/the ledger. `app.MapControllers()` enables attribute routing.

### Settings (runtime-tunable, no restart)
Tabbed **Casino** / **Game**. The admin edits write to the Redis hash **`khela:settings`** (field = config key);
the game engine overlays that hash onto its appsettings base, so changes apply on the next round / within ~15s:
- **Casino** (progression): `ProgressionService.EffectiveCfgAsync` → `ProgressionMath.Overlay(base, dict)`
  (pure, unit-tested; lenient parse + fallback; `Enabled` not overridable). Applies on the next accrual.
- **Game** (timers): `BlackjackTableManager.RuntimeInt(key, default)` — turn / insurance / stalled / disconnect
  seconds, cached ~15s, `>0` guard. Money-safety §5 (never pull a live-stake seat) is unaffected — only the
  threshold moves. `HeartbeatIntervalSeconds` has no consumer → read-only.

### Wallets & Ledger
Search a player (display name / email / `AspNetUsers.Id`) → per-currency balance cards (with gifted-slice) +
a paginated, newest-first ledger of their `WalletTransactions` (time, currency, type, signed amount, balance
after, status, round/table ref). Strictly **read-only**.

---

## Theming
12-palette runtime theme switcher (a floating button injected by `wwwroot/js/theme-switcher.js`, persisted in
`localStorage['khela-theme']`, applied before paint). Themes are `[data-theme]` CSS-variable blocks in
`wwwroot/css/site.css`: green (default), blue, olive, purple, cyan, rose + soft mint/sky/lavender/teal/sand/slate.
Adding a theme = one CSS block + one swatch entry (pure static, no rebuild).

---

## Build / deploy notes (gotchas)
- **Pin EF Core Relational `9.0.8`** in `Khela.Web.csproj` (Pomelo otherwise pulls 9.0.0) or the app throws
  `FileNotFoundException` at startup. The MSB3277 warning is the tell.
- `VipTier` / `ReportStatus` live in `Khela.Common.*`, not `Khela.Game.Database.Models`.
- **Live Settings touch `Khela.Game`** (`ProgressionService`, `BlackjackTableManager`) — rebuild + restart the
  **game backend**, not just the dashboard, for game-side overrides to take effect. Theme/CSS/JS are static.
- **Verified safe:** `Khela.Game.Tests` is 44/44 green (incl. 4 overlay tests + the money-smoke suite).
- **Smoke pattern:** run the built DLL on a throwaway port + authenticate via a PowerShell `WebSession`
  (parse `__RequestVerificationToken`). A running build under VS/IIS Express locks `Khela.Game.dll`, so
  compile-verify to a temp dir (`dotnet build -o`).

---

## TODO / roadmap
- [ ] **Players** module — searchable list → detail (profile, level/XP, wallet summary, recent rounds; link
      "view ledger" → Wallets). Most central next.
- [ ] **Reports** module — moderation queue (list → resolve/dismiss); makes the dashboard alert actionable.
- [ ] **Leaderboards** module — read Redis live boards + MySQL seasons/winners.
- [ ] **Game Tables** module — live table/round state from Redis.
- [ ] Dashboard polish — real period-over-period % deltas (needs daily snapshots), auto-refresh.
- [ ] **Audit log of admin actions** before any write-capable module ships (who changed which setting, when).
- [ ] Account-level theme persistence (currently per-browser `localStorage`).
- [ ] Prod hardening — set `Admin:UserIds`, lock down CORS/HTTPS, move secrets out of `appsettings.json`.
