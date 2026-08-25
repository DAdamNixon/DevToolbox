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
// SEASONS. A seasonal theme is only *painted* inside its window, but choosing one
// never rewrites the saved setting — pick Christmas in December and January
// quietly falls back to the default while the file still says christmas, so next
// December it comes back on its own. "Show all themes" turns the window off
// entirely, which is how you look at Halloween in June.
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
        { id: 'system', season: null, effect: null },
        { id: 'dark', season: null, effect: null },
        { id: 'light', season: null, effect: null }
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
