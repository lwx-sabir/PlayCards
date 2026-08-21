# Moving tuning between environments

The knobs that matter — the pass ladder, the daily ladder, the piggy's pacing, mission and chest catalogs — live in
**Redis**, not in source. None of it travels with a build, and each environment has its own Redis, so tuning done on
one machine simply does not exist on another.

This is how it moves: **export a file locally, ship it with the build, the server applies it on startup.**

## Export (local admin)

**Settings → Transfer**. Tick the groups, add a note, download `khela-settings.json`.

| Group | Carries | Default |
|---|---|---|
| Piggy bank | every `Piggy:` knob | on |
| Monthly pass ladder | `khela:pass` — the whole authored program | on |
| Daily login ladder | `khela:daily` | on |
| Missions & chests | `khela:missions`, `khela:chests` | on |
| Progression, VIP & loyalty | `Progression:` / `Loyalty:` / `Vip:` | off |
| Reward switches | `Rewards:` — **including `BypassAdForMissedDays`** | off |
| Game & table timing | `Blackjack:` / `Table:` | off |

The last two default off deliberately. Table timing is environment-specific — a dev server's shortened windows are
exactly what you don't want carried — and a testing switch should never travel by accident.

**Only knobs that have been SAVED in the admin are in Redis.** Anything still on its appsettings default is not
exported, because the target already has that default. An export of untouched groups is empty, and says so.

## Apply (the server)

Drop the file at `config/khela-settings.json` beside the published API — the path is `Config:SeedFile` in appsettings,
absolute or relative to the content root. On the next start the server reads it and writes it into its own Redis.

Three rules, each the answer to a specific way this goes wrong:

1. **Once per file CONTENT, not once per boot.** The file's hash is recorded in `khela:config:seed`. Re-deploying the
   same build restarts the server; re-applying every time would silently undo tuning done live on that environment
   since — the admin page would appear to work and its changes would vanish at the next restart. Edit the file and it
   applies again.
2. **Merge, never replace.** Only the keys in the file are written; nothing is deleted. A two-group export cannot wipe
   the groups it didn't carry.
3. **Fail soft.** Missing, malformed, unreadable, or a newer format version → log and carry on. Tuning must never be
   able to stop a server booting.

Document keys are restricted to `khela:*`, so a hand-edited file cannot reach anything else sharing the instance.

## Checking what a server is running

`khela:config:seed` holds the provenance: the applied file's `hash`, `appliedAtUtc`, `file`, the `exportedAtUtc` of
the export, its `note`, and how many `entries` were written. Every applied key is also logged individually at startup,
so the log answers "where did this server's tuning come from" without a Redis client.

## Notes

- Redis wins over appsettings at runtime. Once a key is seeded, changing appsettings for that key does nothing until
  the hash field is deleted — which is the point, but worth remembering when a setting "won't change".
- To re-apply a file the server has already seen, edit it (the note field is enough — it changes the hash).
- The admin dashboard is currently local-only. When it ships to the server, an upload/diff route can replace the file
  drop; the seeder stays useful either way, since it carries tuning with a deploy rather than needing someone present.
