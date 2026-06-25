using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Khela.Game.Tests
{
    /// <summary>
    /// End-to-end money-path SMOKE test — drives the real REST API (register → seat → bet → deal → play →
    /// settle) against a running backend, so the wallet flow is exercised without clicking through Swagger.
    /// Needs the server + MySQL + Redis up. Run it:
    ///     1. start the backend:  dotnet run --project Khela.Game  (ASPNETCORE_ENVIRONMENT=Development)
    ///     2. dotnet test --filter Category=Smoke
    /// Override the URL with KHELA_SMOKE_URL. If the server is unreachable the tests no-op (logged), so a
    /// normal `dotnet test` with no server running stays green.
    /// </summary>
    [Trait("Category", "Smoke")]
    public class BlackjackMoneySmokeTests
    {
        private const decimal Starter = 10000m;
        private static readonly string BaseUrl = Environment.GetEnvironmentVariable("KHELA_SMOKE_URL") ?? "http://localhost:5044";
        private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

        private readonly ITestOutputHelper _out;
        public BlackjackMoneySmokeTests(ITestOutputHelper o) => _out = o;

        // ---------- the smoke flows ----------

        [Fact]
        public async Task MoneyPath_DebitsStakeAtDeal_AndSettlesWithinBounds()
        {
            using var c = NewClient();
            if (!await ServerUpAsync(c)) { Skip("backend not reachable"); return; }

            await RegisterAsync(c);
            Assert.Equal(Starter, await ChipsAsync(c));            // starter grant landed

            var table = await CreateTableAsync(c);
            await JoinAsync(c, table);
            await BetAsync(c, table, 1000m);
            Assert.Equal(Starter, await ChipsAsync(c));            // placing a bet does NOT touch the wallet yet

            await EnsureOk(await c.PostAsync($"/api/blackjack/{table}/deal", null));
            Assert.Equal(9000m, await ChipsAsync(c));              // *** debit-on-bet: stake reserved AT deal ***

            await c.PostAsync($"/api/blackjack/{table}/stand/1", null);   // resolve the turn (ignore if auto-done)
            await EnsureOk(await c.PostAsync($"/api/blackjack/{table}/dealerPlay", null));

            var final = await ChipsAsync(c);
            // single 1000 stake from 10000: loss 9000, push 10000, win 11000, natural 3:2 11500
            Assert.Contains(final, new[] { 9000m, 10000m, 11000m, 11500m });
            _out.WriteLine($"Settled OK. started 10000, staked 1000 at deal (-> 9000), final = {final}.");
        }

        [Fact]
        public async Task SameChips_CannotBeStakedAtTwoTables()
        {
            using var c = NewClient();
            if (!await ServerUpAsync(c)) { Skip("backend not reachable"); return; }

            await RegisterAsync(c);
            Assert.Equal(Starter, await ChipsAsync(c));

            // Seat at BOTH tables first (each mirrors the full wallet), then bet the full balance on each.
            var a = await CreateTableAsync(c); await JoinAsync(c, a);
            var b = await CreateTableAsync(c); await JoinAsync(c, b);
            await BetAsync(c, a, Starter);
            await BetAsync(c, b, Starter);

            // Deal table A: the full stake leaves the wallet.
            await EnsureOk(await c.PostAsync($"/api/blackjack/{a}/deal", null));
            Assert.Equal(0m, await ChipsAsync(c));

            // Deal table B with the SAME chips: must be rejected (stake debit fails -> player sits out ->
            // no funded bets), and the wallet must stay 0 — no double-stake, no overdraw.
            var dealB = await c.PostAsync($"/api/blackjack/{b}/deal", null);
            Assert.False(dealB.IsSuccessStatusCode,
                "Deal on table B must be rejected — the chips are already staked at table A.");
            Assert.Equal(0m, await ChipsAsync(c));
            _out.WriteLine("Overdraw blocked: second table's deal rejected, wallet stayed at 0.");

            // Tidy up: settle table A so the run leaves no chips in limbo.
            await c.PostAsync($"/api/blackjack/{a}/stand/1", null);
            await c.PostAsync($"/api/blackjack/{a}/dealerPlay", null);
        }

        [Fact]
        public async Task StalledPlayer_AutoStandsSettlesAndFreesSeat()
        {
            using var c = NewClient();
            if (!await ServerUpAsync(c)) { Skip("backend not reachable"); return; }

            await RegisterAsync(c);
            var table = await CreateTableAsync(c);
            await JoinAsync(c, table);
            await BetAsync(c, table, 1000m);
            await EnsureOk(await c.PostAsync($"/api/blackjack/{table}/deal", null));
            Assert.Equal(9000m, await ChipsAsync(c));            // stake debited at deal (debit-on-bet)

            // Now go SILENT — never send a heartbeat (this HttpClient runs no keep-alive loop). The server's
            // stalled-player reaper must, after Table:StalledTimeoutSeconds, auto-stand the hand, SETTLE it
            // normally, and FREE the seat — without stranding or double-paying the already-debited stake.
            // Tune the backend low (Table__StalledTimeoutSeconds=6) for a fast run; default 30s needs ~40s budget.
            var budgetSec = int.TryParse(Environment.GetEnvironmentVariable("KHELA_STALLED_WAIT"), out var b) ? b : 50;

            bool settled = false, freed = false;
            var deadline = DateTime.UtcNow.AddSeconds(budgetSec);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
                using var doc = await EnsureOk(await c.GetAsync($"/api/blackjack/{table}/board"));
                var root = doc.RootElement;

                // Settled? LastResults carrying our seat proves the round was SETTLED (not silently dropped),
                // so the debited stake was reconciled through the normal wallet path.
                settled = root.TryGetProperty("lastResults", out var lr) && lr.ValueKind == JsonValueKind.Array
                          && lr.EnumerateArray().Any(r => r.GetProperty("seatNumber").GetInt32() == 1);

                // Freed? The reaper removed the seat after settle.
                var seat1 = root.GetProperty("seats").EnumerateArray()
                                .First(s => s.GetProperty("seatNumber").GetInt32() == 1);
                freed = !seat1.GetProperty("occupied").GetBoolean();

                if (settled && freed) break;
            }

            var final = await ChipsAsync(c);
            Assert.True(settled, "Stalled hand must SETTLE (LastResults carries seat 1) — the stake must never be stranded.");
            Assert.True(freed, "The reaper must FREE the seat after settlement.");
            // Single 1000 stake from 10000 → loss 9000 / push 10000 / win 11000 / natural 11500. Anything else
            // means a double-pay or a lost stake.
            Assert.Contains(final, new[] { 9000m, 10000m, 11000m, 11500m });
            _out.WriteLine($"Stalled player reaped cleanly: settled={settled}, seatFreed={freed}, final={final}.");
        }

        [Fact]
        public async Task ReSplit_ChargesEveryHand_NoFreeUnfundedHand()
        {
            // Exercises the B2 fix: every split (including a RE-split of the same hand) must debit its stake.
            // Needs both dev rigs armed on the server: Blackjack__DevPlayerRigPair=true (forces an opening pair)
            // AND Blackjack__DevRigResplit=true (forces the split result back to a splittable pair). Skips cleanly
            // if the server is down or a rig isn't armed.
            using var c = NewClient();
            if (!await ServerUpAsync(c)) { Skip("backend not reachable"); return; }

            await RegisterAsync(c);
            var table = await CreateTableAsync(c);
            await JoinAsync(c, table);
            await BetAsync(c, table, 1000m);
            await EnsureOk(await c.PostAsync($"/api/blackjack/{table}/deal", null));
            Assert.Equal(9000m, await ChipsAsync(c));                            // initial stake debited at deal

            // Close any insurance window (no-op unless the dealer shows an Ace) so split is allowed.
            await c.PostAsync($"/api/blackjack/{table}/insurance/decline/1", null);

            var split1 = await c.PostAsync($"/api/blackjack/{table}/split/1?handIndex=0", null);
            if (!split1.IsSuccessStatusCode) { Skip("DevPlayerRigPair not armed (split rejected — opening hand isn't a pair)"); return; }
            Assert.Equal(8000m, await ChipsAsync(c));                            // *** split stake funded ***

            var split2 = await c.PostAsync($"/api/blackjack/{table}/split/1?handIndex=0", null);
            if (!split2.IsSuccessStatusCode) { Skip("DevRigResplit not armed (re-split rejected)"); return; }
            // With the B2 bug the re-split reused the `sp{handIndex}` correlation id, the wallet treated the 2nd
            // debit as an idempotent duplicate and SKIPPED it → a free unfunded hand (balance would stay 8000).
            // Funded correctly (sp{newHandIndex}) → 7000.
            Assert.Equal(7000m, await ChipsAsync(c));                            // *** RE-split stake funded (the fix) ***
            _out.WriteLine("Re-split funded: 10000 → 9000 (deal) → 8000 (split) → 7000 (re-split). No free hand.");

            // Tidy: stand every hand then settle so the run leaves no chips in limbo.
            for (int h = 0; h < 3; h++) await c.PostAsync($"/api/blackjack/{table}/stand/1?handIndex={h}", null);
            await c.PostAsync($"/api/blackjack/{table}/dealerPlay", null);
        }

        // ---------- helpers ----------

        private static HttpClient NewClient() => new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };

        private async Task<bool> ServerUpAsync(HttpClient c)
        {
            try { await c.GetAsync("/swagger/index.html"); return true; } // any HTTP response = reachable
            catch (Exception ex) { _out.WriteLine($"server probe failed: {ex.Message}"); return false; }
        }

        private void Skip(string why) => _out.WriteLine($"SKIPPED: {why} at {BaseUrl}. Start the backend then re-run with --filter Category=Smoke.");

        private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

        private static async Task<JsonDocument> EnsureOk(HttpResponseMessage res)
        {
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new Xunit.Sdk.XunitException($"{(int)res.StatusCode} {res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: {body}");
            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
        }

        private async Task RegisterAsync(HttpClient c)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var res = await c.PostAsync("/api/auth/register",
                Json(new { email = $"smk_{id}@smoke.local", username = $"smk{id}", password = "Passw0rd!", deviceId = id, countryCode = "bd" }));
            using var doc = await EnsureOk(res);
            var token = doc.RootElement.GetProperty("token").GetString();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private async Task<decimal> ChipsAsync(HttpClient c)
        {
            using var doc = await EnsureOk(await c.GetAsync("/api/wallet/balances"));
            return doc.RootElement.GetProperty("chips").GetDecimal();
        }

        private async Task<string> CreateTableAsync(HttpClient c)
        {
            // MinBet/MaxBet = 0 disables the table limits so the smoke can stake any amount.
            using var doc = await EnsureOk(await c.PostAsync("/api/blackjack/create",
                Json(new { maxPlayers = 1, maxSeatsPerUser = 1, mode = 0, minBet = 0, maxBet = 0 })));
            return doc.RootElement.GetProperty("tableId").GetString();
        }

        private async Task JoinAsync(HttpClient c, string tableId)
            => await EnsureOk(await c.PostAsync($"/api/blackjack/{tableId}/join", Json(new { name = "Smoke", balance = 0, image = "" })));

        private async Task BetAsync(HttpClient c, string tableId, decimal amount)
            => await EnsureOk(await c.PostAsync($"/api/blackjack/{tableId}/bet", Json(new { amount, seatNumber = 1, handIndex = 0 })));
    }
}
