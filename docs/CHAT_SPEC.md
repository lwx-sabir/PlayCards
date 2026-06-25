# Chat Spec — moderation + in-table emotes

*Target: `khela/Khela.Game`. Conventions as in the other specs (DTOs in `Khela.Common`,
interface + DI, XML docs, deliberate migrations, reuse the Redis rate-limit Lua + SignalR
backplane). Existing pieces: `ChatHub` (`SendDm`/`SendChannel`/`JoinChannel`), `ChatService`,
`ChatController`, `IChatModerator` (+ stub `BasicChatModerator`), `ChatMessage` model,
`MessageModerationStatus` enum, ephemeral Redis room chat (ring buffer).*

> NOTE: the **AI moderator API** is a LATER add (the owner will wire it). Build the rule-based
> moderator now, behind a seam that an `AiChatModerator` can slot into. The `report` feature
> itself lives in `PROFILE_SOCIAL_SPEC.md`.

---

## 1. Content moderation pipeline (`IChatModerator`)
Keep the seam; replace the 5-word `BasicChatModerator` stub.

- Contract: `Task<ModerationResult> ModerateAsync(ModerationRequest req)`.
  - `ModerationRequest { string Text; ModerationContext Context; Guid SenderId; string Locale; }`
  - `ModerationContext` enum: `Dm, TableChat, GlobalChat, DisplayName, Bio`.
  - `ModerationResult { ModerationAction Action; string MaskedText; string[] Reasons; double Score; }`
  - `ModerationAction` enum: `Allow, Mask, Flag, Reject`.

- **`RuleBasedChatModerator` (build now):**
  - **Locale-aware profanity/slur lists** for English + **Bengali / Hindi / Urdu**, loaded from
    editable config files (JSON under a `moderation/` config dir) so lists change without a recompile.
  - **Normalize before matching** — lowercase, collapse repeated chars, strip separators/leetspeak
    (`f.u.c.k`, `f u c k`, `phuck`) to resist bypass.
  - **PII / contact-info detection** → `Reject` (or `Flag`): phone numbers (incl. local formats),
    emails, URLs, and app keywords (`whatsapp, telegram, imo, signal, insta, number`).
  - **Chip-for-cash solicitation detection** → `Flag`/`Reject`: `sell/buy chips, bkash, nagad,
    taka, cash, paypal, price` near chips/buy/sell. *Protects the legal social-casino posture —
    real-money chip trading is what makes it look like gambling.*

- **AI seam (LATER, do NOT implement the API now):** define so an `AiChatModerator` (external API)
  and a `CompositeChatModerator` (rule-based first for cheap hard blocks → AI for nuance) can be
  added behind a config flag `Moderation:AiEnabled` (default false). Ship rule-based now with the
  composite wiring stubbed/ready.

- **Apply moderation to ALL of:** DM, table chat, global chat, **display name**, **bio**
  (display-name/bio writes are in `PROFILE_SOCIAL_SPEC.md` but call this moderator).
  Behavior: `Reject` = block send + tell user; `Mask` = deliver masked; `Flag` = deliver + record
  for review (create an `AutoFlag` report row — see report model in the profile/social spec).
  Log every Flag/Reject.

- **Persist room chat for evidence:** table/global chat is ephemeral, so abuse has no trail.
  Persist room messages to a lightweight `RoomChatMessage` table (`ChannelType, ChannelId,
  SenderId, Body, SentAt, ModerationStatus`) with a retention/purge policy (e.g. 90 days), OR at
  minimum snapshot the offending + surrounding messages into a report's `ContextSnapshot` on
  report. Prefer persistence — needed for moderation, disputes, and app-store review.

- **Age gate (policy):** `BirthDate` exists on `ApplicationUser` but chat doesn't check it.
  Restrict minors (under local age) to **quick-chat/emotes only** — no free-text DM/global.
  (Device-guest birthdates are unreliable; confirm policy, default to the safer setting.)

---

## 2. In-table emotes / pre-made quick chat (safe-by-design)
Pre-approved phrases + emotes players send at the table. Because they're **fixed IDs, not free
text, they need no moderation and can't be abused** — minor-safe, and great for casual play.

- **Server-defined catalog** `QuickChatCatalog` (config/JSON): each entry `{ id, kind
  (Phrase|Emote), text/emojiKey, minVipTier?, premiumItemId? }`. Examples: "Nice hand!",
  "Good luck!", "Wow!", thumbs-up, clap, laugh.
- Hub: `ChatHub.SendQuickChat(string tableId, string quickChatId)` → validate caller is seated
  (reuse `IsUserSeatedAsync`) + validate `quickChatId` exists → broadcast `{ senderId, quickChatId }`
  to group `table:{tableId}`. Ephemeral (no DB).
- **Rate-limit** (reuse limiter; e.g. 3 per 3s) + optional per-emote cooldown to stop spam.
- Future (backlog): premium/cosmetic emotes as sellable items.

---

## Definition of done
- `RuleBasedChatModerator` replaces the stub; applied to DM/room/displayName/bio.
- Tests: normalization/bypass; English + ≥1 Bengali/Hindi case; PII detection; chip-for-cash
  detection.
- AI seam present but disabled (`Moderation:AiEnabled` default false); no external API called.
- Room chat persisted (or snapshot-on-report) with a retention note; age-gate applied.
- Quick-chat broadcasts by ID only; rejects unseated callers + unknown IDs; rate-limited.
- `dotnet build` passes; deliberate migrations; no NON-NEGOTIABLE weakened.
