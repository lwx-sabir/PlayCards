// Khela Admin — dashboard data loader. Pulls the read-only stats APIs and fills the cards + table.
(function () {
    function num(n) { return (Number(n) || 0).toLocaleString('en-US'); }
    function chips(n) {
        n = Number(n) || 0;
        if (n >= 1e9) return strip(n / 1e9) + 'B';
        if (n >= 1e6) return strip(n / 1e6) + 'M';
        if (n >= 1e3) return strip(n / 1e3) + 'K';
        return num(Math.round(n));
    }
    function strip(x) { return x.toFixed(x < 100 ? 1 : 0).replace(/\.0$/, ''); }
    function set(id, v) { var el = document.getElementById(id); if (el) el.textContent = v; }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }
    function getJson(url) {
        return fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }

    function loadUsers() {
        getJson('/api/stats/users').then(function (d) {
            if (!d) return;
            set('st-total-players', num(d.totalPlayers));
            set('st-new-week', '▲ ' + num(d.newThisWeek) + ' this week');
            set('st-chips-float', chips(d.chipsInCirculation));
        });
    }
    function loadGames() {
        getJson('/api/stats/games').then(function (d) {
            if (!d) return;
            set('st-wagered-24h', chips(d.chipsWagered24h));
            set('st-wagered-foot', num(d.betsPlaced24h) + ' bets placed');
            set('st-rounds-24h', num(d.rounds24h));
            set('st-rounds-foot', num(d.rounds7d) + ' this week');
        });
    }
    function loadRecent() {
        var body = document.getElementById('recent-players-body');
        if (!body) return;
        getJson('/api/stats/users/recent').then(function (rows) {
            if (!rows || !rows.length) return;
            body.innerHTML = rows.map(function (p) {
                var ini = (p.displayName || '?').charAt(0).toUpperCase();
                var st = p.active ? 'active' : 'idle';
                var label = p.active ? 'Active' : 'Idle';
                return '<tr>' +
                    '<td><div class="cell-user"><div class="avatar">' + esc(ini) + '</div>' +
                        '<div><div class="nm">' + esc(p.displayName) + '</div>' +
                        '<div class="dim" style="font-size:12px">' + esc(p.region) + '</div></div></div></td>' +
                    '<td><span class="badge badge-plan">Level ' + (p.level | 0) + '</span></td>' +
                    '<td>' + num(Math.round(p.chips)) + '</td>' +
                    '<td><span class="pill ' + st + '">' + label + '</span></td>' +
                    '</tr>';
            }).join('');
        });
    }

    function fmtDate(s) { var d = new Date(s); return isNaN(d) ? '' : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }); }
    function loadChart() {
        var line = document.getElementById('chart-line');
        var area = document.getElementById('chart-area');
        var dot = document.getElementById('chart-dot');
        if (!line) return;
        getJson('/api/stats/games/wagered-series').then(function (pts) {
            if (!pts || !pts.length) return;
            var W = 720, H = 230, top = 20, bot = 205, n = pts.length;
            var max = Math.max.apply(null, pts.map(function (p) { return Number(p.wagered) || 0; }));
            if (max <= 0) max = 1;
            function X(i) { return n === 1 ? 0 : (i / (n - 1)) * W; }
            function Y(v) { return bot - ((Number(v) || 0) / max) * (bot - top); }
            var d = pts.map(function (p, i) { return (i ? 'L' : 'M') + X(i).toFixed(1) + ',' + Y(p.wagered).toFixed(1); }).join(' ');
            line.setAttribute('d', d);
            if (area) area.setAttribute('d', d + ' L' + W + ',' + H + ' L0,' + H + ' Z');
            var peak = 0;
            for (var i = 1; i < n; i++) if ((Number(pts[i].wagered) || 0) > (Number(pts[peak].wagered) || 0)) peak = i;
            if (dot) { dot.setAttribute('cx', X(peak).toFixed(1)); dot.setAttribute('cy', Y(pts[peak].wagered).toFixed(1)); }
            var ax = document.getElementById('chart-xaxis');
            if (ax) {
                var idx = [0, Math.round(n * 0.2), Math.round(n * 0.4), Math.round(n * 0.6), Math.round(n * 0.8), n - 1];
                ax.innerHTML = idx.map(function (i) { return '<span>' + fmtDate(pts[Math.min(i, n - 1)].date) + '</span>'; }).join('');
            }
        });
    }

    var ALERT_ICONS = {
        crit: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M12 8v4M12 16h.01"/></svg>',
        warn: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M10.3 3.7L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.7a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/></svg>',
        ok: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.1V12a10 10 0 1 1-5.9-9.1"/><path d="M22 4L12 14.01l-3-3"/></svg>'
    };
    var ALERT_LABELS = { crit: 'Critical', warn: 'Warning', ok: 'Healthy' };
    function loadAlerts() {
        var body = document.getElementById('alerts-body');
        if (!body) return;
        getJson('/api/stats/alerts').then(function (rows) {
            if (!rows || !rows.length) return;
            body.innerHTML = rows.map(function (a) {
                var sev = (a.severity === 'crit' || a.severity === 'warn') ? a.severity : 'ok';
                return '<div class="alert-item">' +
                    '<span class="alert-ico ' + sev + '">' + ALERT_ICONS[sev] + '</span>' +
                    '<div><span class="alert-tag ' + sev + '">' + ALERT_LABELS[sev] + '</span>' +
                    '<div class="alert-text">' + esc(a.detail) + '</div></div>' +
                    '<span class="when">' + esc(a.when) + '</span>' +
                    '</div>';
            }).join('');
        });
    }

    function init() { loadUsers(); loadGames(); loadRecent(); loadChart(); loadAlerts(); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
