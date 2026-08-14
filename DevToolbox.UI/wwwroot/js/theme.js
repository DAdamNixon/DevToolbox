// Applies the colour theme by setting data-theme on <html>; theme.css does the rest.
//
// Two stores are involved, and only one of them is authoritative:
//
//   ui_settings.yaml  the real setting, owned by UiSettingsService, hand-editable
//                     like every other DevToolbox config
//   localStorage      a cache of that value, and nothing more
//
// The cache exists purely for timing. This script runs from <head>, long before
// Blazor has started or a file has been read, and the theme has to be on the
// element by then or the window paints dark and then snaps to light. So boot()
// applies the cached value immediately and Blazor calls apply() a moment later
// with the value from YAML, which corrects the cache if someone edited the file
// by hand. Clearing browser storage costs one frame of the wrong colour, not the
// setting.
(function () {
    'use strict';

    var STORAGE_KEY = 'devtoolbox.theme';
    var VALID = ['system', 'dark', 'light'];

    // WebView2 hands out a real localStorage, but it is backed by the user data
    // folder and can be absent or throwing if that folder is unwritable. The
    // theme must still apply in that case, so every access is guarded and a
    // failure just means no cache.
    function read() {
        try {
            var stored = window.localStorage.getItem(STORAGE_KEY);
            return VALID.indexOf(stored) !== -1 ? stored : 'system';
        } catch (e) {
            return 'system';
        }
    }

    function write(theme) {
        try {
            window.localStorage.setItem(STORAGE_KEY, theme);
        } catch (e) {
            /* No cache available; YAML still holds the setting. */
        }
    }

    function systemPrefersDark() {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    }

    // theme.css only knows "light" and the default, so "system" is resolved here
    // rather than with a prefers-color-scheme block. That keeps an explicit
    // choice from having to out-specify a media query.
    function paint(theme) {
        var resolved = theme === 'system' ? (systemPrefersDark() ? 'dark' : 'light') : theme;
        document.documentElement.setAttribute('data-theme', resolved);
    }

    var mediaListenerAttached = false;

    function watchSystem() {
        if (mediaListenerAttached || !window.matchMedia) return;
        mediaListenerAttached = true;
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
            // Only "system" follows the OS; an explicit choice must not be
            // overwritten when the user flips their Windows theme.
            if (read() === 'system') paint('system');
        });
    }

    window.devtoolboxTheme = {
        // Called inline from <head>. Paints from the cache before first paint.
        boot: function () {
            paint(read());
            watchSystem();
            return true;
        },

        // Called from Blazor with the value loaded from ui_settings.yaml, and
        // again on every change in Settings so the preview is immediate.
        apply: function (theme) {
            if (VALID.indexOf(theme) === -1) theme = 'system';
            write(theme);
            paint(theme);
            watchSystem();
            return theme;
        },

        // The stored preference ('system' | 'dark' | 'light'), not the resolved one.
        current: read,

        // What is actually on screen right now — 'dark' or 'light'. Used by
        // components that need to pick an asset rather than a colour.
        resolved: function () {
            return document.documentElement.getAttribute('data-theme') || 'dark';
        }
    };
})();
