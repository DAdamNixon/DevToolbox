// Applies the colour theme by setting data-theme on <html>; theme.css and
// themes.css do the rest. Also decides whether animation is allowed at all, and
// publishes that two ways: to themeEffects.js, which builds the particle layer, and
// as data-fx-motion on <html>, which is how the CSS-only ornament in themeDecor.css
// knows whether it may move.
//
// Two stores are involved, and only one of them is authoritative:
//
//   ui_settings.yaml  the real settings, owned by UiSettingsService, hand-editable
//                     like every other DevToolbox config
//   localStorage      a cache of those values, and nothing more
//
// The cache exists purely for timing. This script runs from <head>, long before
// Blazor has started or a file has been read, and the theme has to be on the
// element by then or the window paints dark and then snaps to light. So boot()
// applies the cached values immediately and Blazor calls apply() a moment later
// with the values from YAML, which corrects the cache if someone edited the file
// by hand. Clearing browser storage costs one frame of the wrong colour, not the
// settings.
//
// SEASONS. Two separate things, and they are easy to confuse:
//
//   'seasonal'          a rule rather than a palette. It resolves by date, so the
//                       theme changes itself when the calendar does — this is the
//                       only setting that ever repaints without being asked.
//   any one season      an explicit choice, painted only inside its own window.
//                       Choosing it never rewrites the saved setting: pick
//                       Christmas in December and January quietly falls back to
//                       the default while the file still says christmas, so next
//                       December it comes back on its own.
//
// "Show all themes" is about neither. It only decides whether the dropdown lists
// a season out of its window, which is how you look at Halloween in June.
(function () {
    'use strict';

    var KEY_THEME = 'devtoolbox.theme';
    var KEY_ANIMATIONS = 'devtoolbox.themeAnimations';
    var KEY_SHOW_ALL = 'devtoolbox.showAllThemes';

    var DEFAULT_THEME = 'system';

    // A catalog is required, but a missing themeCatalog.js must not take the
    // window down with it — falling back to the three original themes keeps the
    // app usable and loses only the seasons.
    var CATALOG = (window.devtoolboxThemes && window.devtoolboxThemes.themes) || [
        { id: 'system', season: null, effect: null, auto: false },
        { id: 'dark', season: null, effect: null, auto: false },
        { id: 'light', season: null, effect: null, auto: false }
    ];

    function find(id) {
        for (var i = 0; i < CATALOG.length; i++) {
            if (CATALOG[i].id === id) return CATALOG[i];
        }
        return null;
    }

    // WebView2 hands out a real localStorage, but it is backed by the user data
    // folder and can be absent or throwing if that folder is unwritable. The
    // theme must still apply in that case, so every access is guarded and a
    // failure just means no cache.
    function readString(key, fallback, valid) {
        try {
            var stored = window.localStorage.getItem(key);
            if (stored === null) return fallback;
            return valid(stored) ? stored : fallback;
        } catch (e) {
            return fallback;
        }
    }

    function readBool(key, fallback) {
        var raw = readString(key, null, function (v) { return v === 'true' || v === 'false'; });
        return raw === null ? fallback : raw === 'true';
    }

    function write(key, value) {
        try {
            window.localStorage.setItem(key, String(value));
        } catch (e) {
            /* No cache available; YAML still holds the settings. */
        }
    }

    function storedTheme() {
        return readString(KEY_THEME, DEFAULT_THEME, function (v) { return !!find(v); });
    }

    // Animations default on: the only themes that have one are seasonal, so a
    // user who went and picked Christmas has already opted in to the mood.
    function storedAnimations() { return readBool(KEY_ANIMATIONS, true); }

    function storedShowAll() { return readBool(KEY_SHOW_ALL, false); }

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    // Month-and-day only, compared as 'MM-DD' text — lexicographic order is
    // calendar order for that format, which is the whole reason it is stored that
    // way. A window whose end sorts before its start wraps the new year.
    function inSeason(season, now) {
        if (!season) return true;

        var parts = String(season).split('..');
        if (parts.length !== 2) return true;

        var today = pad(now.getMonth() + 1) + '-' + pad(now.getDate());
        return parts[0] <= parts[1]
            ? today >= parts[0] && today <= parts[1]
            : today >= parts[0] || today <= parts[1];
    }

    // How many days a window covers, both ends included. Measured in a leap year so
    // that 29 February — the far end of Winter — is a real date rather than a case to
    // special-case. Mirrors ThemeSeason.LengthInDays on the C# side.
    function seasonLength(season) {
        var parts = String(season).split('..');
        if (parts.length !== 2) return Infinity;

        var from = dayOfLeapYear(parts[0]);
        var to = dayOfLeapYear(parts[1]);

        return from <= to ? to - from + 1 : 366 - from + to + 1;
    }

    function dayOfLeapYear(monthDay) {
        var bits = monthDay.split('-');
        var start = Date.UTC(2024, 0, 1);
        var day = Date.UTC(2024, Number(bits[0]) - 1, Number(bits[1]));

        return Math.round((day - start) / 86400000) + 1;
    }

    // What the 'seasonal' theme means today: of the themes whose window contains this
    // date and which are allowed to be chosen automatically, the one with the shortest
    // window. Nesting does the work — Halloween sits inside Fall, (Better)Christmas
    // inside Winter — and the narrower window is always the more specific answer.
    // Null when nothing is in season, which is most of the spring and summer.
    function seasonalPick(now) {
        var best = null;
        var bestLength = Infinity;

        for (var i = 0; i < CATALOG.length; i++) {
            var def = CATALOG[i];
            if (!def.season || !def.auto || !inSeason(def.season, now)) continue;

            var length = seasonLength(def.season);
            if (length < bestLength) {
                best = def;
                bestLength = length;
            }
        }

        return best;
    }

    function systemPrefersDark() {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    }

    // The theme that should actually be on the element: an unknown or out-of-season
    // id collapses to the OS preference, and 'system' is resolved here rather than
    // with a prefers-color-scheme block so that an explicit choice never has to
    // out-specify a media query.
    function effective(theme, showAll) {
        var def = find(theme);
        if (!def) return systemPrefersDark() ? 'dark' : 'light';

        // 'seasonal' is a rule, not a palette: it resolves to whichever seasonal theme
        // the date calls for, and to the system theme the rest of the year. showAll is
        // not consulted — that setting is about what the dropdown lists, and this
        // choice is about what the calendar says.
        if (def.id === 'seasonal') {
            var pick = seasonalPick(new Date());
            return pick ? pick.id : (systemPrefersDark() ? 'dark' : 'light');
        }

        if (def.season && !showAll && !inSeason(def.season, new Date())) {
            return systemPrefersDark() ? 'dark' : 'light';
        }
        return def.id === 'system' ? (systemPrefersDark() ? 'dark' : 'light') : def.id;
    }

    // themeEffects.js is optional — it queues its own work until the body exists,
    // and if the file is missing the app simply never animates.
    function driveEffects(painted, animations) {
        if (!window.devtoolboxThemeEffects) return;

        var def = find(painted);
        window.devtoolboxThemeEffects.set(animations && def ? def.effect : null);
    }

    // The particle layer is built by JS, so themeEffects.js can simply not be asked
    // for it. Decoration that lives entirely in CSS — the twinkling lights
    // (Better)Christmas wraps its cards in — has no such switch, so the setting is
    // published onto <html> as well and themeDecor.css keys its animations off it.
    // Without this the "Theme animations" toggle would stop the snow and leave the
    // lights blinking, which reads as the toggle being broken rather than partial.
    function paint(theme, animations, showAll) {
        var painted = effective(theme, showAll);
        document.documentElement.setAttribute('data-theme', painted);
        document.documentElement.setAttribute('data-fx-motion', animations ? 'on' : 'off');
        driveEffects(painted, animations);
        return painted;
    }

    var seasonTimer = null;

    // "Arrives on its own" has to survive the session it arrives during: this is a
    // desktop window that stays open for days, and without this the first of December
    // would only be noticed at the next launch.
    //
    // Half-hourly rather than a timer set for midnight: a single long timer is the one
    // a sleeping laptop misses, and this costs a date comparison. Nothing is repainted
    // unless the resolved theme actually changed, so the particle layer is not rebuilt
    // for the sake of it.
    function watchSeason() {
        if (seasonTimer) { return; }

        seasonTimer = window.setInterval(function () {
            if (storedTheme() !== 'seasonal') { return; }

            var next = effective(storedTheme(), storedShowAll());
            if (next === document.documentElement.getAttribute('data-theme')) { return; }

            paint(storedTheme(), storedAnimations(), storedShowAll());
        }, 30 * 60 * 1000);
    }

    var mediaListenerAttached = false;

    function watchSystem() {
        if (mediaListenerAttached || !window.matchMedia) return;
        mediaListenerAttached = true;
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
            // Repainting from the stored preference rather than forcing a theme:
            // an explicit choice comes out of effective() unchanged, so flipping
            // the Windows theme moves only what actually resolves through the OS
            // — 'system', and a seasonal pick that is currently out of season.
            paint(storedTheme(), storedAnimations(), storedShowAll());
        });
    }

    window.devtoolboxTheme = {
        // Called inline from <head>. Paints from the cache before first paint.
        boot: function () {
            paint(storedTheme(), storedAnimations(), storedShowAll());
            watchSystem();
            watchSeason();
            return true;
        },

        // Called from Blazor with the values loaded from ui_settings.yaml, and
        // again on every change in Settings so the preview is immediate. The two
        // flags are optional so a caller that only knows about the theme — an
        // older page, or a console poke — keeps whatever is cached.
        apply: function (theme, animations, showAll) {
            if (!find(theme)) theme = DEFAULT_THEME;
            if (animations === undefined || animations === null) animations = storedAnimations();
            if (showAll === undefined || showAll === null) showAll = storedShowAll();

            write(KEY_THEME, theme);
            write(KEY_ANIMATIONS, !!animations);
            write(KEY_SHOW_ALL, !!showAll);

            var painted = paint(theme, !!animations, !!showAll);
            watchSystem();
            watchSeason();
            return painted;
        },

        // The stored preference, not the resolved one — this can name a seasonal
        // theme that is not currently on screen.
        current: storedTheme,

        // What is actually painted right now — a concrete theme id, never
        // 'system'. Used by components that need to pick an asset rather than a
        // colour, and by anything that wants to know whether the season landed.
        resolved: function () {
            return document.documentElement.getAttribute('data-theme') || 'dark';
        }
    };
})();
