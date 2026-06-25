# Profile & Social Spec (for the build agent)

*Target: `khela/Khela.Game` (.NET 8, MySQL/Pomelo, Redis, SignalR). Conventions: DTOs in
`Khela.Common`; interface + DI registration; XML docs; deliberate EF migrations (never rename
columns); `RowVersion` concurrency; rate-limit new endpoints (reuse the Redis Lua limiter).
Don't weaken any NON-NEGOTIABLE rule in `CLAUDE.md`. `dotnet build` must pass with no
unexpected pending migration.*

Scope = exactly these five items. (Chat content-moderation + emotes are a separate doc:
`CHAT_SPEC.md`. Achievements/badges/extra gifts are in `SOCIAL_BACKLOG.md`.)

---

## 1. ProfileController — read/edit profiles (the #1 gap)
The rich profile data (`UserProfile`, `UserGameStats`) is currently unreadable by clients, and
nothing writes the editable fields after signup. Add `Controllers/ProfileController.cs`
(`[Authorize]`) + DTOs in `Khela.Common`.

- **`GET /api/profile/me`** → own profile: `DisplayName, AvatarId, AvatarFrameId, CountryFlagId,
  Region, Level, Experience, VipTier, LoyaltyPoints, Bio, StatusMessage, CreatedAt (join date),
  LastSeenAt, FriendCount`, plus stats from `UserGameStats`/`UserProfile`
  (`GamesPlayed, GamesWon, WinRate (derived), BiggestWin, NetProfit, CurrentWinStreak,
  LongestWinStreak`) and public linked socials.
- **`GET /api/profile/{userId}`** → another player's **PUBLIC** profile. EXCLUDE private fields
  (email, mobile, address; hide exact net worth if you deem it sensitive). Include relationship
  (`IsFriend`, pending-request state) and `IsOnline` (from `PresenceService`).
  **Block-aware:** if either party has a `Blocked` `Friendship` edge, return 404/limited.
- **`PATCH /api/profile/me`** → edit `DisplayName, AvatarId, AvatarFrameId, CountryFlagId, Bio,
  StatusMessage`.
  - `DisplayName`: enforce uniqueness via `DisplayNameNormalized`; moderate it (see §3); `RowVersion`.
  - `AvatarId/FrameId/FlagId`: **only allow equipping owned cosmetics** — check entitlements. If
    the cosmetics-ownership table isn't built yet, gate behind a TODO and allow only the
    free/default set for now (don't let a client equip arbitrary ids).
  - `Bio/StatusMessage`: moderate on write (see §3).

## 2. Bio / status message
Add to `Database/Models/UserProfile.cs` (+ EF migration):
- `Bio (string, maxLength 160, nullable)` — "about me".
- `StatusMessage (string, maxLength 80, nullable)` — short status line.
Both returned by the profile GETs and editable via PATCH; both moderated on write (§3).

## 3. Wire `LastSeenAt` + moderate display names
- **`LastSeenAt`:** on `ChatHub.OnDisconnectedAsync`, when `PresenceService` reports the user's
  **last** connection dropped, set `UserProfile.LastSeenAt = UtcNow`. Optionally bump on
  login/major activity, **debounced** (at most once / 60s — don't write every action).
- **Moderate display names:** run `DisplayName` through the content moderator on **creation**
  (`AuthController.EnsureProfileAndStarterAsync`) AND on PATCH, so offensive/PII names never enter
  the system or get broadcast in chat/leaderboard DTOs. Use the moderator defined in
  `CHAT_SPEC.md`; until that exists, a basic profanity/length check is acceptable as a stopgap
  (same check also applies to Bio/StatusMessage).

## 4. Exclude blocked users from search/recent (bug fix)
`FriendsService.SearchAsync` and `RecentPlayersAsync` currently still return blocked users. Add a
shared "is blocked between A and B" filter (a `Blocked` `Friendship` edge in **either** direction)
and apply it to both queries.

## 5. Report a message or player
New model `Database/Models/Report.cs` (+ EF migration):
- `Id (Guid)`, `ReporterUserId (Guid)`, `ReportedUserId (Guid)`,
  `TargetType` enum `{Player, Message}`, `TargetMessageId (Guid?)` (for persisted DMs),
  `ContextSnapshot (longtext)` — JSON of the offending message(s) + a little surrounding context,
  captured at report time (**required** because room chat is ephemeral),
  `Reason` enum `{Harassment, HateSpeech, Sexual, Spam, Solicitation, ContactInfo, Cheating, Other}`,
  `Details (string ≤500)`, `Status` enum `{Open, Reviewing, ActionTaken, Dismissed}` (default Open),
  `Source` enum `{User, AutoFlag}`, `CreatedAt`, `ResolvedAt (DateTime?)`, `ResolvedByAdminId (Guid?)`,
  `ActionNote (string)`. Indexes: `(ReportedUserId, Status)`, `(Status, CreatedAt)`.
- Endpoints (`Controllers/ReportsController.cs`, `[Authorize]`):
  - `POST /api/reports/message` `{ reportedUserId, targetMessageId?, contextSnapshot, reason, details }`
  - `POST /api/reports/player` `{ reportedUserId, reason, details }`
  - Validate reporter ≠ reported; dedupe identical open reports from the same reporter; **rate-limit**
    (e.g. 5/min) to stop report spam.
- Admin (admin-role only — bundle with the existing pre-prod admin-gating TODO): `GET
  /api/admin/reports?status=` (paged) + `POST /api/admin/reports/{id}/resolve` `{ action, note }`.

---

## Definition of done
- `dotnet build` passes; migrations added deliberately; snapshot in sync.
- Profile GET (self + other, block-aware) and PATCH work; cosmetics equip gated to owned/default.
- `Bio`/`StatusMessage` added, returned, editable, moderated.
- `LastSeenAt` updates on last-disconnect; display names moderated at creation + edit.
- Blocked users excluded from search/recent.
- Report endpoints create rows; admin can list + resolve (behind admin role); rate-limited.
- No NON-NEGOTIABLE weakened; no private account fields leaked in public profile responses.
