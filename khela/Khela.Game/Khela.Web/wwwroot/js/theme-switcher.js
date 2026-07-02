// Khela Admin — self-contained theme switcher.
// Applies the saved palette immediately (no flash) and injects a floating theme button.
// Themes are CSS [data-theme] blocks in site.css; this only flips the attribute + persists the choice.
(function () {
    var KEY = 'khela-theme';
    var THEMES = [
        ['green', '#bdf24a', 'Green'],
        ['blue', '#5b8cff', 'Blue'],
        ['olive', '#cdbb4e', 'Olive'],
        ['purple', '#a87bff', 'Purple'],
        ['cyan', '#34d6e6', 'Cyan'],
        ['rose', '#ff6ea0', 'Rose'],
        ['mint', '#88e0b4', 'Mint'],
        ['sky', '#8ac6f2', 'Sky'],
        ['lavender', '#bcacf0', 'Lavender'],
        ['teal', '#6fcfc4', 'Teal'],
        ['sand', '#e2c98c', 'Sand'],
        ['slate', '#a2b5d6', 'Slate']
    ];

    function saved() { try { return localStorage.getItem(KEY) || 'green'; } catch (e) { return 'green'; } }
    function apply(t) { document.documentElement.setAttribute('data-theme', t); }

    // run NOW (script is in <head>, before <body> paints) so there's no flash of the default theme
    apply(saved());

    function build() {
        if (document.querySelector('.theme-fab')) return;

        var swatches = THEMES.map(function (t) {
            return '<button class="swatch" type="button" data-set-theme="' + t[0] +
                   '" style="--sw:' + t[1] + '" title="' + t[2] + '"></button>';
        }).join('');

        var fab = document.createElement('div');
        fab.className = 'theme-fab';
        fab.innerHTML =
            '<button class="theme-fab-btn" type="button" title="Change theme" aria-label="Change theme">' +
                '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' +
                    '<circle cx="13.5" cy="6.5" r="1.1"/><circle cx="17.5" cy="10.5" r="1.1"/>' +
                    '<circle cx="8.5" cy="7.5" r="1.1"/><circle cx="6.5" cy="12.5" r="1.1"/>' +
                    '<path d="M12 2a10 10 0 1 0 0 20c1.7 0 2.5-1.2 2.5-2.3 0-1.6-1.4-2-1.4-3.2 0-.8.7-1.5 1.6-1.5H17a5 5 0 0 0 5-5c0-4.7-4.5-8-10-8z"/>' +
                '</svg>' +
            '</button>' +
            '<div class="theme-pop"><div class="theme-pop-title">Theme</div><div class="swatches">' + swatches + '</div></div>';
        document.body.appendChild(fab);

        var btn = fab.querySelector('.theme-fab-btn');
        var pop = fab.querySelector('.theme-pop');
        function mark(t) {
            fab.querySelectorAll('.swatch').forEach(function (s) {
                s.classList.toggle('active', s.getAttribute('data-set-theme') === t);
            });
        }
        mark(saved());

        btn.addEventListener('click', function (e) { e.stopPropagation(); pop.classList.toggle('open'); });
        pop.addEventListener('click', function (e) { e.stopPropagation(); });
        document.addEventListener('click', function () { pop.classList.remove('open'); });
        fab.querySelectorAll('.swatch').forEach(function (s) {
            s.addEventListener('click', function () {
                var t = s.getAttribute('data-set-theme');
                apply(t);
                try { localStorage.setItem(KEY, t); } catch (e) {}
                mark(t);
                pop.classList.remove('open');
            });
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', build);
    else build();
})();
