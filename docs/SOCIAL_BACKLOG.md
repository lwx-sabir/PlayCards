# Social backlog (deferred — not now)

Parked items, to spec/build later. Not part of `PROFILE_SOCIAL_SPEC.md` or `CHAT_SPEC.md`.

- **Achievements / badges** — model + award rules (milestones, win streaks, first blackjack,
  VIP tiers) + profile display + endpoints.
- **Gift variety** — gift cosmetics/items, custom amounts, and gift messages. The `Gift` model
  already has `Currency/Amount/Message`; only a fixed daily chips packet is wired today. Keep the
  token **non-giftable**.
- **AI moderator API** — `AiChatModerator` + `CompositeChatModerator` behind `Moderation:AiEnabled`
  (owner will wire the external API). Seam is prepared in `CHAT_SPEC.md`.
- **Follow / followers** (currently only mutual friendship).
- **Mute** (separate from block) + extend block to silence room chat (block only affects DMs today).
- **Chat retention/purge policy** finalization; profile visit/like; premium emote items.
