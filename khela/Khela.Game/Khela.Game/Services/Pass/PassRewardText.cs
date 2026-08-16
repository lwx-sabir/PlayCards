using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Khela.Common.Rewards;
using Khela.Game.Services.Rewards;

namespace Khela.Game.Services.Pass
{
    /// <summary>
    /// A one-line text form for a reward payload, so 31 days × 2 tracks can be authored in the admin page without a
    /// forest of dropdowns:
    ///
    /// <code>Chips 2000, Kash 15, XP 100, Chest CK_Chest:Rare, Item lottery_ticket x3</code>
    ///
    /// A reward may carry up to <see cref="RewardGrant.MaxImages"/> artwork urls, appended after <c>@</c> and
    /// separated by <c>|</c> (back layer first):
    ///
    /// <code>Chips 2000 @icons/chip.png|icons/glow.png</code>
    ///
    /// Round-trips: <see cref="Format"/>(<see cref="Parse"/>(s)) is stable, so an edit that changes nothing leaves the
    /// stored JSON untouched. PURE and unit-tested — the admin page just calls it and shows the error verbatim.
    /// </summary>
    public static class PassRewardText
    {
        /// <summary>Render lines back to the editable text form.</summary>
        public static string Format(IEnumerable<RewardGrant> lines)
        {
            if (lines == null) return string.Empty;
            return string.Join(", ", lines.Where(l => l != null).Select(One));

            string One(RewardGrant l)
            {
                string body;
                switch (l.Kind)
                {
                    case RewardKind.Currency: body = $"{l.Id} {Num(l.Amount)}"; break;
                    case RewardKind.Xp: body = $"XP {Num(l.Amount)}"; break;
                    default: body = l.Amount == 1m ? $"{l.Kind} {l.Id}" : $"{l.Kind} {l.Id} x{Num(l.Amount)}"; break;
                }
                return (l.Images == null || l.Images.Count == 0) ? body : body + " @" + string.Join("|", l.Images);
            }
        }

        /// <summary>
        /// Parse the text form. Returns null and sets <paramref name="error"/> on the FIRST bad token — the caller
        /// shows it as-is, so the message has to name the token.
        /// </summary>
        public static List<RewardGrant> Parse(string text, out string error)
        {
            error = null;
            var lines = new List<RewardGrant>();
            if (string.IsNullOrWhiteSpace(text)) return lines;

            foreach (var raw in text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                // Peel off the artwork suffix (" @back.png|front.png") before the reward itself is parsed, so a url
                // containing digits or a colon can never be mistaken for an amount or a chest tier.
                List<string> images = null;
                var at = token.IndexOf('@');
                if (at >= 0)
                {
                    var urls = token.Substring(at + 1).Trim();
                    token = token.Substring(0, at).Trim();
                    images = urls.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
                    if (images.Count == 0) images = null;
                    if (images != null && images.Count > RewardGrant.MaxImages)
                    { error = $"'{raw.Trim()}' — at most {RewardGrant.MaxImages} images per reward."; return null; }
                    if (token.Length == 0) { error = $"'{raw.Trim()}' — images need a reward in front of them."; return null; }
                }

                var parts = token.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { error = $"'{token}' — write a kind then a value, e.g. \"Chips 1000\"."; return null; }

                var head = parts[0];

                // Names only, never numbers: Enum.TryParse happily turns "3" into a RewardKind, and CurrencyType has
                // the same hazard, so a numeric head could silently mean something the author never typed.
                if (head.All(char.IsDigit))
                { error = $"'{token}' — name the reward (e.g. \"Chips 1000\"), don't use a number."; return null; }

                // XP 100
                if (head.Equals("XP", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryAmount(parts[1], out var xp) || xp <= 0) { error = $"'{token}' — XP needs a positive amount."; return null; }
                    { var g = RewardGrant.Xp((long)xp); g.Images = images; lines.Add(g); }
                    continue;
                }

                // Chest CK_Chest:Rare [x2] · Item lottery_ticket [x3] · Cosmetic sku_hat
                if (Enum.TryParse<RewardKind>(head, ignoreCase: true, out var kind) && kind != RewardKind.Currency && kind != RewardKind.Xp)
                {
                    var id = parts[1];
                    decimal count = 1m;
                    if (parts.Length > 2)
                    {
                        var c = parts[2].TrimStart('x', 'X');
                        if (!TryAmount(c, out count) || count <= 0) { error = $"'{token}' — the count after 'x' must be a positive number."; return null; }
                    }
                    if (kind == RewardKind.Chest && !RewardIds.TryParseChest(id, out _, out _))
                    { error = $"'{token}' — a chest is \"Key:Tier\", e.g. Chest CK_Chest:Rare."; return null; }
                    lines.Add(new RewardGrant { Kind = kind, Id = id, Amount = count, Images = images });
                    continue;
                }

                // Chips 1000 · Kash 5 — a currency NAME, never a number (so "3" can't become Tokens).
                if (!RewardCurrencies.TryParse(head, out var currency))
                { error = $"'{token}' — '{head}' isn't a reward kind or currency (use {RewardCurrencies.AllowedList}, XP, Chest, Item, Cosmetic)."; return null; }
                if (!RewardCurrencies.IsAllowed(currency))
                {
                    error = RewardCurrencies.IsForbidden(currency)
                        ? $"'{token}' — '{currency}' can never be a reward (tradeable token)."
                        : $"'{token}' — '{currency}' is not a permitted reward currency (allowed: {RewardCurrencies.AllowedList}).";
                    return null;
                }
                if (!TryAmount(parts[1], out var amount) || amount <= 0)
                { error = $"'{token}' — {currency} needs a positive amount."; return null; }

                { var g = RewardGrant.Currency(currency.ToString(), amount); g.Images = images; lines.Add(g); }
            }
            return lines;
        }

        private static bool TryAmount(string s, out decimal value)
            => decimal.TryParse((s ?? string.Empty).Replace(",", string.Empty).Replace("_", string.Empty),
                                NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        private static string Num(decimal d) => d == decimal.Truncate(d)
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString(CultureInfo.InvariantCulture);
    }
}
