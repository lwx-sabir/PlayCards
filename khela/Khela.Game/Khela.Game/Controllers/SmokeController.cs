// DEV-ONLY money-path smoke. Gated on BOTH IsDevelopment() AND a "Smoke:Enabled" config flag that is set
// ONLY in Properties/launchSettings.json (a dev-launch file that is never deployed) — two independent
// guards, so it cannot be reached in production, while still working in any LOCAL build config (Debug or
// Release). Two separate mistakes would be needed to expose this money-mutating endpoint.
using CardGames.Blackjack;
using Khela.Common.Blackjack;
using Khela.Game.Database.Models;
using Khela.Game.Managers;
using Khela.Game.Services.Wallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// DEV-ONLY money-path smoke. GET /smoke serves a one-button page; POST /smoke/run plays a full
    /// blackjack round through the REAL <see cref="BlackjackTableManager"/> + <see cref="IWalletService"/>
    /// (register → seat → bet → deal → settle) and an overdraw scenario, returning a pass/fail log.
    /// Only present in Debug builds and gated to the Development environment; throwaway users are deleted
    /// after each run so repeated runs don't accumulate accounts.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("smoke")]
    public class SmokeController : ControllerBase
    {
        private const decimal Starter = 10000m;

        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly BlackjackTableManager _tables;
        private readonly IWalletService _wallet;
        private readonly UserManager<ApplicationUser> _users;

        public SmokeController(IWebHostEnvironment env, IConfiguration config, BlackjackTableManager tables,
            IWalletService wallet, UserManager<ApplicationUser> users)
        {
            _env = env;
            _config = config;
            _tables = tables;
            _wallet = wallet;
            _users = users;
        }

        // Both guards must hold: a Development environment AND an explicit opt-in flag that lives only in
        // launchSettings.json (never in a deployed build). Off by default everywhere else.
        private bool Enabled => _env.IsDevelopment() && _config.GetValue("Smoke:Enabled", false);

        [HttpGet]
        public IActionResult Page()
            => Enabled ? Content(Html, "text/html") : NotFound();

        [HttpPost("run")]
        public async Task<IActionResult> Run()
        {
            if (!Enabled) return NotFound();

            var log = new SmokeLog();
            try { await RunMoneyPathAsync(log); }
            catch (Exception ex) { log.Fail("money-path crashed", ex.Message); }
            try { await RunOverdrawAsync(log); }
            catch (Exception ex) { log.Fail("overdraw scenario crashed", ex.Message); }

            return Ok(new { ok = log.AllOk, steps = log.Steps });
        }

        // ---- scenarios ----

        private async Task RunMoneyPathAsync(SmokeLog log)
        {
            var user = await NewUserWithChipsAsync();
            try
            {
                log.Check("starter grant = 10000", await Chips(user) == Starter, $"chips={await Chips(user)}");

                var t = (await _tables.CreateTableAsync(1, 1, BlackjackMode.Classic, 0, 0)).TableId;
                log.Pass("create single-seat table", t);

                await _tables.AddPlayerAsync(t, new Player(user.Id, 0m, "Smoke"));
                log.Pass("seat player", "seat 1");

                await _tables.PlaceBetAsync(t, user.Id, 1, 1000m, 0);
                log.Check("placing a bet does NOT touch the wallet", await Chips(user) == Starter, $"chips={await Chips(user)} (expected 10000)");

                await _tables.DealAsync(t);
                var afterDeal = await Chips(user);
                log.Check("DEBIT-ON-BET: stake leaves the wallet at deal", afterDeal == 9000m, $"chips={afterDeal} (expected 9000)");

                try { await _tables.StandAsync(t, user.Id, 1, 0); } catch { /* hand may have auto-resolved */ }
                await _tables.DealerPlayAndSettleAsync(t, user.Id);

                var final = await Chips(user);
                var outcome = final == 9000m ? "loss" : final == 10000m ? "push" : final == 11000m ? "win" : final == 11500m ? "blackjack 3:2" : "??";
                log.Check("settle credits the correct gross", new[] { 9000m, 10000m, 11000m, 11500m }.Contains(final), $"final={final} ({outcome})");
            }
            finally { await TryDeleteUserAsync(user); }
        }

        private async Task RunOverdrawAsync(SmokeLog log)
        {
            var user = await NewUserWithChipsAsync();
            try
            {
                var a = (await _tables.CreateTableAsync(1, 1, BlackjackMode.Classic, 0, 0)).TableId;
                var b = (await _tables.CreateTableAsync(1, 1, BlackjackMode.Classic, 0, 0)).TableId;
                await _tables.AddPlayerAsync(a, new Player(user.Id, 0m, "Smoke"));
                await _tables.AddPlayerAsync(b, new Player(user.Id, 0m, "Smoke"));
                await _tables.PlaceBetAsync(a, user.Id, 1, Starter, 0);   // stake the FULL balance on both tables
                await _tables.PlaceBetAsync(b, user.Id, 1, Starter, 0);

                await _tables.DealAsync(a);
                log.Check("table A reserves the full stake", await Chips(user) == 0m, $"chips={await Chips(user)} (expected 0)");

                var rejected = false; var detail = "";
                try { await _tables.DealAsync(b); }
                catch (Exception ex) { rejected = true; detail = ex.Message; }
                log.Check("OVERDRAW BLOCKED: same chips can't be staked at table B", rejected, detail);
                log.Check("no double-debit after the rejected deal", await Chips(user) == 0m, $"chips={await Chips(user)} (expected 0)");

                try { await _tables.DealerPlayAndSettleAsync(a, user.Id); } catch { /* tidy up */ }
            }
            finally { await TryDeleteUserAsync(user); }
        }

        // ---- helpers ----

        private async Task<ApplicationUser> NewUserWithChipsAsync()
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var user = new ApplicationUser { UserName = $"smk{id}", Email = $"smk_{id}@smoke.local", CountryCode = "bd" };
            var created = await _users.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException("user create failed: " + string.Join(", ", created.Errors.Select(e => e.Description)));

            await _wallet.CreditAsync(user.Id, CurrencyType.Chips, Starter, TransactionType.Bonus,
                $"smoke-starter:{user.Id}", new WalletContext { Description = "smoke starter" });
            return user;
        }

        // Best-effort cleanup so repeated dev runs don't accumulate throwaway Identity accounts. The
        // wallet/ledger/audit rows are keyed by the user's GUID (not an FK cascade) and are harmless dev data.
        private async Task TryDeleteUserAsync(ApplicationUser user)
        {
            try { await _users.DeleteAsync(user); } catch { /* leave it; it's a dev throwaway */ }
        }

        private Task<decimal> Chips(ApplicationUser user) => _wallet.GetBalanceAsync(user.Id, CurrencyType.Chips);

        private sealed class SmokeLog
        {
            public List<object> Steps { get; } = new();
            public bool AllOk { get; private set; } = true;

            public void Pass(string name, string detail = "") => Steps.Add(new { name, ok = true, detail });
            public void Fail(string name, string detail = "") { AllOk = false; Steps.Add(new { name, ok = false, detail }); }
            public void Check(string name, bool ok, string detail = "") { if (!ok) AllOk = false; Steps.Add(new { name, ok, detail }); }
        }

        private const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Khela — Money-Path Smoke</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; font-family: ui-sans-serif, system-ui, Segoe UI, Roboto, sans-serif;
         background: #0b0f17; color: #e6edf3; display: flex; min-height: 100vh; align-items: flex-start; justify-content: center; }
  .wrap { width: min(820px, 94vw); margin: 6vh 0 8vh; }
  h1 { font-size: 20px; font-weight: 650; margin: 0 0 4px; letter-spacing: .2px; }
  .sub { color: #8b98a9; font-size: 13px; margin: 0 0 20px; }
  .bar { display: flex; gap: 14px; align-items: center; margin-bottom: 16px; }
  button { background: #2563eb; color: #fff; border: 0; border-radius: 9px; padding: 11px 20px;
           font-size: 14px; font-weight: 600; cursor: pointer; transition: background .15s; }
  button:hover { background: #1d4ed8; }
  button:disabled { background: #2a3342; color: #6b7686; cursor: default; }
  .banner { flex: 1; text-align: center; padding: 10px; border-radius: 9px; font-weight: 700; font-size: 14px;
            border: 1px solid transparent; }
  .banner.idle { color: #6b7686; }
  .banner.running { background: #1f2937; color: #d6b94c; border-color: #3b4252; }
  .banner.pass { background: #0f2a17; color: #4ade80; border-color: #1f6b38; }
  .banner.fail { background: #2a0f13; color: #f87171; border-color: #6b1f27; }
  .logwin { background: #060a12; border: 1px solid #1c2434; border-radius: 11px; padding: 14px 16px;
         font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 13px; line-height: 1.7;
         min-height: 240px; max-height: 60vh; overflow-y: auto; white-space: pre-wrap; }
  .ln { padding: 1px 0; }
  .ln.ok   { color: #56d364; }
  .ln.bad  { color: #ff7b72; }
  .ln.info { color: #7d8ea3; }
</style>
</head>
<body>
  <div class="wrap">
    <h1>Khela — Money-Path Smoke</h1>
    <p class="sub">Plays a full blackjack round through the real wallet (debit-on-bet → settle) plus an overdraw check. Dev-only.</p>
    <div class="bar">
      <button id="run">Run smoke ▶</button>
      <div id="banner" class="banner idle">idle — press run</div>
    </div>
    <div id="log" class="logwin"></div>
  </div>
<script>
  const btn = document.getElementById('run');
  const logEl = document.getElementById('log');
  const banner = document.getElementById('banner');
  function line(cls, text) {
    const d = document.createElement('div');
    d.className = 'ln ' + cls;
    d.textContent = text;
    logEl.appendChild(d);
    logEl.scrollTop = logEl.scrollHeight;
  }
  btn.onclick = async () => {
    btn.disabled = true;
    logEl.innerHTML = '';
    banner.className = 'banner running';
    banner.textContent = 'running…';
    line('info', 'POST /smoke/run …');
    const t0 = performance.now();
    try {
      const res = await fetch('/smoke/run', { method: 'POST' });
      const data = await res.json();
      for (const s of data.steps) {
        line(s.ok ? 'ok' : 'bad', (s.ok ? '✓' : '✗') + '  ' + s.name + (s.detail ? '   —   ' + s.detail : ''));
      }
      const ms = Math.round(performance.now() - t0);
      line('info', 'done in ' + ms + ' ms');
      banner.className = 'banner ' + (data.ok ? 'pass' : 'fail');
      banner.textContent = data.ok ? 'PASS — money path healthy' : 'FAIL — see log';
    } catch (e) {
      line('bad', '✗  request failed: ' + e.message);
      banner.className = 'banner fail';
      banner.textContent = 'FAIL — request error';
    } finally {
      btn.disabled = false;
    }
  };
</script>
</body>
</html>
""";
    }
}
