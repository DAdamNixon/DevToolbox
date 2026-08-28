// The seasonal ornament that has to be an element rather than a background image.
//
// HOW THIS DIFFERS FROM themeEffects.js. That file builds weather: dozens of identical
// particles, all the same two elements, drifting across the window and interacting with
// nothing. This one builds *props* — things that sit in a particular place, and in four
// cases things you can poke:
//
//   halloween        cobwebs in the corners of the content, and the cauldron on Settings
//   thanksgiving     the cornucopia, and the turkey it fires
//   easter           the grass along the footer, and the eggs hidden in it
//   fourth-of-july   the flag on its pole, and the fireworks a click sets off
//
// Everything static and tiled stays in css/themeDecor.css as a background image on the
// footer or the shell — the leaf pile, the candles, the pumpkin, the skater. A prop only
// comes here when it needs its own box (so it can be hovered, or transformed
// independently of a neighbour) or its own lifetime (a spark, a firework, a turkey).
//
// WHAT IS AND IS NOT GATED ON MOTION. The layer itself is built for whatever theme is
// painted, so the cobwebs hang and the eggs still appear under the grass with animations
// turned off — those are ornament, not motion, and the rule css/themeDecor.css already
// follows is that decoration stays and the flourish stops. What *is* gated is anything
// transient: a firework, a spark, a turkey in flight. With motion off those simply do not
// spawn, because a particle that cannot move is not a shorter version of itself, it is a
// dot sitting on the screen.
//
// ACCESSIBILITY. The layer is aria-hidden, and the interactive props inside it are plain
// divs with no tabindex on purpose. A focusable control inside an aria-hidden subtree is
// worse than an unreachable one: a keyboard user lands on something a screen reader has
// been told does not exist. None of this is a feature of the app — it is a turkey — so
// pointer-only is the honest answer rather than a compromise.
//
// Called only by theme.js, which decides which theme is painted. Being asked for a theme
// with no props is normal and quiet.
(function () {
    'use strict';

    var LAYER_ID = 'theme-decor';

    // theme.js runs from <head> and calls in before there is a body to append to, so the
    // first request is held until the document is ready. Only the latest is kept: two
    // calls before load means the second is the answer. Same contract as themeEffects.js.
    var pending = null;
    var ready = false;

    // The theme currently built. Every delegated listener below is registered once, for
    // the life of the window, and reads this instead of being added and removed — a
    // listener that no-ops is cheaper than one that has to be tracked well enough to
    // detach, and there is exactly one of it.
    var current = null;

    function rand(min, max) { return min + Math.random() * (max - min); }

    function el(tag, cls) {
        var node = document.createElement(tag);
        if (cls) { node.className = cls; }
        return node;
    }

    function layer() { return document.getElementById(LAYER_ID); }

    // Both switches, the same pair css/themeDecor.css consults: the app's "Theme
    // animations" setting, which theme.js publishes as data-fx-motion, and the OS
    // preference, which wins either way.
    function mayMove() {
        if (document.documentElement.getAttribute('data-fx-motion') === 'off') { return false; }
        if (!window.matchMedia) { return true; }
        return !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    // Transient props are removed on a timer rather than on animationend. animationend
    // does not fire for an element whose animation never started — a display:none
    // ancestor, a theme switched mid-flight, a tab backgrounded at the wrong moment — and
    // each of those would leak one node per click for as long as the window is open.
    function reap(node, afterMs) {
        window.setTimeout(function () {
            if (node.parentNode) { node.parentNode.removeChild(node); }
        }, afterMs);
    }

    /* ── halloween ──────────────────────────────────────────────────────────────────
       Four webs, one drawing, three flips.

       They hang in the corners of the *content* rather than of the window, which is why
       the CSS insets them past the nav and the status bar. The nav is translucent with a
       backdrop blur and sits at z-30 — above this layer — so a web in the true top-left
       corner would either be smeared by that blur or drawn over the brand mark. The
       content's corners are unambiguous and nothing has to be moved out of the way. */
    function buildWebs(host) {
        // The top two only. Four was the first version and it read as an effect applied to
        // the window rather than as cobwebs in a room — and the bottom pair fought the
        // candles for the same strip of screen. Webs collect where nothing disturbs them.
        var corners = ['tl', 'tr'];
        for (var i = 0; i < corners.length; i++) {
            host.appendChild(el('i', 'decor-web is-' + corners[i]));
        }
    }

    /* ── thanksgiving ───────────────────────────────────────────────────────────────
       The cornucopia, and the turkey.

       Charge is measured from pointerdown to pointerup and clamped at CHARGE_MAX, so a
       held button is a fuller horn and a leant-on button is not a bug. Everything the
       shot varies — distance, height, spin, size, duration — is one custom property on
       the projectile, because the arc itself is a keyframe in CSS and this file's job is
       only to say how far. */
    var CHARGE_MAX = 1400;

    var charging = null;

    function chargeOf(startedAt) {
        return Math.min((Date.now() - startedAt) / CHARGE_MAX, 1);
    }

    function launchTurkey(horn, charge) {
        var host = layer();
        if (!host) { return; }

        var muzzle = horn.getBoundingClientRect();

        // Never a limp shot: a tap still clears a good part of the window, and the charge
        // buys the rest. A power of 0 would drop the turkey on the footer, which reads as
        // the click not having worked.
        var power = 0.4 + charge * 0.6;

        var turkey = el('i', 'decor-turkey');
        turkey.style.cssText = [
            'left:' + Math.round(muzzle.right - 12) + 'px',
            'top:' + Math.round(muzzle.top + muzzle.height * 0.25) + 'px',
            '--tk-size:' + Math.round(26 + power * 20) + 'px',
            '--tk-dx:' + Math.round(power * window.innerWidth * 0.9) + 'px',
            // The one number that decides the shape of the whole arc. tg-launch samples a
            // parabola against it and lands at +0.907 of it, which from a launch point this
            // close to the footer is always past the bottom of the window — so there is no
            // separate --tk-fall to disagree with this one.
            '--tk-rise:' + Math.round(power * window.innerHeight * 0.6) + 'px',
            // Brisker than it was. At 1.4-2.3s the turkey floated; a shot wants to feel
            // thrown, and the arc is the part worth watching rather than the loiter at the
            // top of it.
            '--tk-dur:' + (1.05 + power * 0.6).toFixed(2) + 's',
            '--tk-spin:' + (Math.random() < 0.5 ? '-' : '') + (1 + Math.round(power * 2)) + 'turn'
        ].join(';');

        host.appendChild(turkey);
        reap(turkey, 3200);
    }

    /* ── easter ─────────────────────────────────────────────────────────────────────
       Tufts along the footer, each with an egg behind it.

       The reveal is a CSS :hover on the tuft and nothing else — no listener, no state.
       All this does is scatter them and hand each one a colour, which is the same
       division of labour themeEffects.js has with its stylesheet.

       Positions are jittered around an even spacing rather than drawn at random across
       the width: pure random on twelve tufts reliably produces two overlapping and a bare
       stretch beside them. */
    var EGG_COLORS = [
        '#f4a7c0', '#f6d67a', '#a9d9f0', '#c9aee8',
        '#f2a878', '#9fe0bd', '#f0e6a8', '#e9a8d4'
    ];

    // The one random number behind Easter's pastel cards.
    //
    // css/themeDecor.css gives each card position a fixed offset from this base, so the
    // card palette is a random rotation of one fixed wheel: different every time the theme
    // is painted, and — this is the point — identical for every render in between. A hue
    // rolled per card per render would flicker through the spectrum every time Blazor
    // re-rendered the dashboard, which it does on every keystroke in the filter box.
    //
    // Written to <html> rather than to the layer because the cards are not in the layer.
    // It is left behind when the theme changes, which is harmless: nothing else reads it.
    function seedCardHues() {
        document.documentElement.style.setProperty(
            '--easter-hue-base', Math.floor(Math.random() * 360) + 'deg');
    }

    var TUFTS = 12;

    function buildTufts(host) {
        seedCardHues();

        for (var i = 0; i < TUFTS; i++) {
            var tuft = el('i', 'decor-tuft');
            var slot = (i + 0.5) / TUFTS * 100;

            tuft.style.cssText = [
                'left:' + (slot + rand(-2.6, 2.6)).toFixed(2) + 'vw',
                '--tuft-size:' + rand(0.82, 1.25).toFixed(2),
                '--egg:' + EGG_COLORS[i % EGG_COLORS.length],
                // A row of tufts leaning the same way is a comb, not a lawn.
                '--tuft-lean:' + rand(-9, 9).toFixed(1) + 'deg'
            ].join(';');

            tuft.appendChild(el('b'));
            host.appendChild(tuft);
        }
    }

    /* ── fourth of july ─────────────────────────────────────────────────────────────
       The flag, and the fireworks.

       The flag is two nested elements rather than one: the pole has to stay rigid while
       the cloth ripples, and one element cannot hold two unrelated transforms. */
    function buildFlag(host) {
        var pole = el('i', 'decor-flagpole');
        pole.appendChild(el('b', 'decor-flag'));
        host.appendChild(pole);
    }

    var SPARK_COLORS = ['#ff5a63', '#ffffff', '#7ea8ff', '#ffd36e', '#ff8f9a', '#c8dcff'];

    var SPARKS_PER_BURST = 20;

    // One burst per this many ms. A double-click or a drag across the window would
    // otherwise queue a dozen bursts, which is both a lot of nodes and — more to the
    // point — a wall of sparks instead of a firework.
    var BURST_GAP = 200;

    var lastBurst = 0;

    function firework(x, y) {
        var host = layer();
        if (!host) { return; }

        var now = Date.now();
        if (now - lastBurst < BURST_GAP) { return; }
        lastBurst = now;

        var shell = el('i', 'decor-burst');
        shell.style.cssText = 'left:' + Math.round(x) + 'px;top:' + Math.round(y) + 'px';

        // One hue per burst, not per spark: a real firework is one colour with a white
        // core, and mixing six colours in one shell reads as confetti.
        var hue = SPARK_COLORS[Math.floor(Math.random() * SPARK_COLORS.length)];

        for (var i = 0; i < SPARKS_PER_BURST; i++) {
            var spark = el('b');

            // Evenly spaced around the circle with a little jitter, rather than a random
            // angle each: twenty random angles leave gaps and clumps, and a firework is
            // the one thing everybody knows the shape of.
            var angle = (i / SPARKS_PER_BURST) * Math.PI * 2 + rand(-0.12, 0.12);
            var reach = rand(46, 118);

            spark.style.cssText = [
                '--bx:' + (Math.cos(angle) * reach).toFixed(1) + 'px',
                '--by:' + (Math.sin(angle) * reach).toFixed(1) + 'px',
                '--bd:' + rand(0.85, 1.3).toFixed(2) + 's',
                '--bc:' + (i % 5 === 0 ? '#ffffff' : hue)
            ].join(';');

            shell.appendChild(spark);
        }

        host.appendChild(shell);
        reap(shell, 1800);
    }

    /* ── the cauldron ───────────────────────────────────────────────────────────────
       The one prop that is not in this layer at all.

       It lives at the bottom of the Settings page, in Settings.razor, because that is
       where it was asked to be and because a fixed overlay cannot sit at the end of a
       scrolling document. Blazor owns that markup and re-renders it freely, so nothing
       here holds a reference to it: the listeners are on the document and find it by
       attribute each time, which also means it works the moment the page is navigated to
       and needs no teardown when it is navigated away from.

       Stirring is measured as swept angle rather than as pointer distance. Distance
       rewards a straight drag across the pot, which is not stirring; angle only advances
       if you actually go round. */

    // Radians of sweep per colour step. A little under a quarter turn, so a lap gives
    // four or five changes — fast enough to feel like a reward, slow enough that the
    // colour is legible between steps.
    var SWEEP_PER_STEP = 1.35;

    var stir = null;

    function cauldronAngle(pot, event) {
        var box = pot.getBoundingClientRect();
        return Math.atan2(
            event.clientY - (box.top + box.height * 0.5),
            event.clientX - (box.left + box.width * 0.5));
    }

    function spark(pot) {
        if (!mayMove()) { return; }

        var well = pot.querySelector('.decor-cauldron-sparks');
        if (!well) { return; }

        var count = 3 + Math.floor(Math.random() * 3);
        for (var i = 0; i < count; i++) {
            var bit = el('b');
            bit.style.cssText = [
                '--sx:' + rand(-30, 30).toFixed(1) + 'px',
                '--sy:' + rand(-64, -30).toFixed(1) + 'px',
                // 4–8px. Three was more literally a spark and very nearly invisible at
                // the size the pot is drawn.
                '--ss:' + rand(4, 8).toFixed(1) + 'px',
                '--sd:' + rand(0.6, 1.05).toFixed(2) + 's'
            ].join(';');
            well.appendChild(bit);
            reap(bit, 1400);
        }
    }

    function beginStir(pot, event) {
        stir = {
            pot: pot,
            angle: cauldronAngle(pot, event),
            swept: 0,
            hue: Number(pot.getAttribute('data-hue') || 118)
        };

        pot.classList.add('is-stirring');

        // Capture, so the stir survives the pointer leaving the pot — which it will, since
        // stirring is a circle and the pot is not very big.
        if (pot.setPointerCapture) {
            try { pot.setPointerCapture(event.pointerId); } catch (e) { /* not a captureable pointer */ }
        }
    }

    function moveStir(event) {
        if (!stir) { return; }

        var pot = stir.pot;
        var angle = cauldronAngle(pot, event);

        // Shortest way round, so crossing the -pi/pi seam is not counted as most of a lap.
        var delta = angle - stir.angle;
        while (delta > Math.PI) { delta -= Math.PI * 2; }
        while (delta < -Math.PI) { delta += Math.PI * 2; }

        stir.angle = angle;
        stir.swept += Math.abs(delta);

        pot.style.setProperty('--spoon-angle', (angle * 180 / Math.PI).toFixed(1) + 'deg');

        while (stir.swept >= SWEEP_PER_STEP) {
            stir.swept -= SWEEP_PER_STEP;

            // 47 degrees a step: coprime with 360, so the walk visits a long run of
            // distinct hues instead of cycling through the same four.
            stir.hue = (stir.hue + 47) % 360;
            pot.setAttribute('data-hue', String(stir.hue));
            pot.style.setProperty('--brew-hue', String(stir.hue));
            spark(pot);
        }
    }

    function endStir() {
        if (!stir) { return; }
        stir.pot.classList.remove('is-stirring');
        stir = null;
    }

    /* ── building and tearing down ──────────────────────────────────────────────── */

    var BUILD = {
        halloween: buildWebs,
        thanksgiving: function (host) { host.appendChild(el('i', 'decor-horn')); },
        easter: buildTufts,
        'fourth-of-july': buildFlag
    };

    function apply(theme) {
        var existing = layer();

        if (existing) {
            // Already built for this theme. Rebuilding would re-roll every tuft position
            // for no reason, which reads as a glitch when Settings re-applies the same
            // theme after a save.
            if (existing.getAttribute('data-decor') === theme) { return; }
            existing.parentNode.removeChild(existing);
        }

        current = theme;

        var build = BUILD[theme];
        if (!build) { return; }

        var host = el('div');
        host.id = LAYER_ID;
        host.setAttribute('data-decor', theme);
        // Ornament with no meaning: kept out of the accessibility tree entirely rather
        // than left for a screen reader to announce twelve tufts of grass.
        host.setAttribute('aria-hidden', 'true');

        build(host);
        document.body.appendChild(host);
    }

    /* ── the listeners, registered once ────────────────────────────────────────── */

    function watch() {
        // The cornucopia and the cauldron both start on pointerdown, and both are inside
        // the same delegated handler so that only one of them can ever claim a press.
        document.addEventListener('pointerdown', function (event) {
            if (event.button !== 0) { return; }

            var target = event.target;
            if (!target || !target.closest) { return; }

            if (current === 'thanksgiving') {
                var horn = target.closest('.decor-horn');
                if (horn) {
                    charging = { horn: horn, at: Date.now() };
                    horn.classList.add('is-charging');
                    return;
                }
            }

            if (current === 'halloween') {
                var pot = target.closest('[data-decor="cauldron"]');
                if (pot) {
                    beginStir(pot, event);
                    // A stir is a drag, and without this the browser starts a text
                    // selection across the Settings page as soon as the circle begins.
                    event.preventDefault();
                }
            }
        });

        document.addEventListener('pointermove', moveStir);

        document.addEventListener('pointerup', function () {
            if (charging) {
                var horn = charging.horn;
                var charge = chargeOf(charging.at);
                charging = null;
                horn.classList.remove('is-charging');

                if (mayMove()) { launchTurkey(horn, charge); }
            }

            endStir();
        });

        // A pointer that is cancelled — the window losing focus mid-press, a touch
        // becoming a scroll — must not leave the horn glowing or the pot mid-stir.
        document.addEventListener('pointercancel', function () {
            if (charging) {
                charging.horn.classList.remove('is-charging');
                charging = null;
            }
            endStir();
        });

        // Fireworks. click rather than pointerdown, so a drag to select text or to move a
        // card is not also a firework, and the burst lands where the click completed.
        document.addEventListener('click', function (event) {
            if (current !== 'fourth-of-july' || !mayMove()) { return; }

            var target = event.target;

            // Not while typing or picking. A shower of sparks over the field you are
            // filling in is the point at which a theme stops being a theme.
            if (target && target.closest && target.closest('input, textarea, select, [contenteditable="true"]')) { return; }

            firework(event.clientX, event.clientY);
        });

        // A resize while a turkey is in flight would leave it aimed at the old window, and
        // the flag and the tufts are positioned in vw so they follow on their own. Nothing
        // to do for either — noted so the absence is deliberate rather than forgotten.
    }

    window.devtoolboxThemeDecor = {
        // theme is the resolved theme id — never 'system' or 'seasonal' — or null to strip
        // the layer entirely.
        set: function (theme) {
            theme = theme || null;

            if (!ready) {
                pending = theme;
                return;
            }
            apply(theme);
        }
    };

    function start() {
        ready = true;
        watch();
        apply(pending);
        pending = null;
    }

    // Appended to <body>, so the layer outlives Blazor's re-renders of #app and does not
    // need to be rebuilt on navigation.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
