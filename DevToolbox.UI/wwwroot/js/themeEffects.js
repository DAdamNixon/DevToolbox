// The seasonal animation layer: builds the particles, and nothing else.
//
// This file owns randomness and DOM count. It does not know what a snowflake
// looks like, which way it falls, or what colour it is — all of that is in
// css/themeEffects.css, keyed off the data-fx attribute set here. That split is
// what makes a new effect a CSS block plus a line of catalog rather than a code
// change: every particle is the same two elements carrying the same --fx-*
// properties, whatever it ends up depicting.
//
// Called only by theme.js, which decides *whether* to animate from the active
// theme and the user's setting. Being asked for an effect that does not exist,
// or for none at all, is normal and quiet.
(function () {
    'use strict';

    var CONTAINER_ID = 'theme-fx';

    var CONFIG = (window.devtoolboxThemes && window.devtoolboxThemes.effects) || {};

    // theme.js runs from <head> and calls in before there is a body to append to,
    // so the first request is held until the document is ready. Only the latest is
    // kept: two calls before load means the second is the answer.
    var pending = null;
    var ready = false;

    function rand(min, max) {
        return min + Math.random() * (max - min);
    }

    // One particle. Every value that varies is a custom property rather than a
    // concrete style, so the stylesheet decides what to do with it — --fx-drift is
    // horizontal travel for snow and vertical wander for a bat, from the same
    // number.
    function particle(cfg) {
        var el = document.createElement('span');
        el.className = 'fx';

        var duration = rand(cfg.minDuration, cfg.maxDuration);

        el.style.cssText = [
            // Slightly past both edges: a column of particles starting exactly at
            // x=0 leaves a visible clean margin down the left of the window.
            '--fx-x:' + rand(-3, 101).toFixed(2) + 'vw',
            '--fx-y:' + rand(0, 80).toFixed(2) + 'vh',
            '--fx-size:' + rand(cfg.minSize, cfg.maxSize).toFixed(1) + 'px',
            '--fx-duration:' + duration.toFixed(2) + 's',
            // Negative, and up to a whole pass long, so every particle is already
            // mid-flight on the first frame. Without this the effect announces
            // itself as a curtain dropping from the top of the window every time
            // the theme is switched.
            '--fx-delay:' + (-rand(0, duration)).toFixed(2) + 's',
            '--fx-drift:' + rand(-cfg.maxDrift, cfg.maxDrift).toFixed(2) + 'vw',
            '--fx-sway:' + rand(4, 14).toFixed(1) + 'px',
            '--fx-sway-duration:' + rand(2.5, 6).toFixed(2) + 's',
            // WHOLE TURNS, and that is the whole point of the unit. This was
            // rand(180, 720) degrees, which made the tumble's end orientation
            // different from its start — so an infinite, non-alternating rotation
            // snapped back to zero once per cycle and every leaf visibly jerked
            // every 2.5–6 seconds. A whole number of turns is visually identical to
            // none, so the same animation now loops without a seam. Do not relax
            // this to an arbitrary angle.
            '--fx-spin:' + (Math.random() < 0.5 ? '-' : '') + Math.round(rand(1, 2)) + 'turn',
            '--fx-spin-duration:' + rand(3, 8).toFixed(2) + 's',
            '--fx-opacity:' + rand(0.45, 0.95).toFixed(2)
        ].join(';');

        // The glyph goes on the inner element: the outer one is already spending
        // its transform on the descent, and a single element cannot run two
        // independent animations on the same property.
        el.appendChild(document.createElement('i'));
        return el;
    }

    function build(id) {
        var cfg = CONFIG[id];
        if (!cfg) return null;

        var host = document.createElement('div');
        host.id = CONTAINER_ID;
        host.setAttribute('data-fx', id);
        // Decoration with no meaning: kept out of the accessibility tree entirely
        // rather than left for a screen reader to read a hundred snowflakes.
        host.setAttribute('aria-hidden', 'true');

        var batch = document.createDocumentFragment();
        for (var i = 0; i < cfg.count; i++) {
            batch.appendChild(particle(cfg));
        }
        host.appendChild(batch);

        return host;
    }

    function apply(id) {
        var existing = document.getElementById(CONTAINER_ID);

        if (existing) {
            // Already running this effect. Rebuilding would re-roll every position
            // for no reason, which looks like a glitch when Settings re-applies the
            // same theme after a save.
            if (existing.getAttribute('data-fx') === id) return;
            existing.parentNode.removeChild(existing);
        }

        if (!id) return;

        var host = build(id);
        if (host) document.body.appendChild(host);
    }

    window.devtoolboxThemeEffects = {
        // id is an effect from the catalog, or null/'' to stop.
        set: function (id) {
            id = id || null;

            if (!ready) {
                pending = id;
                return;
            }
            apply(id);
        }
    };

    function start() {
        ready = true;
        apply(pending);
        pending = null;
    }

    // Appended to <body>, so it outlives Blazor's re-renders of #app and does not
    // need to be rebuilt on navigation.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
