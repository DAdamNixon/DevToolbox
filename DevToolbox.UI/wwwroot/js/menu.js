// Closes the open card menu when you click anywhere that is not a menu.
//
// This started as a full-screen backdrop element, which is the version of this you write when you do
// not want JavaScript. It does not survive contact with these cards. A backdrop only works if it sits
// above the page and below the menu, and "above the page" is not something a z-index can promise here:
//
//   .ws-actions       opacity: 0.45          — opacity below 1 creates a stacking context
//   .modern-card      hover:-translate-y-1   — a transform creates one too
//   .workspace-card   hover:scale-[1.02]     — and another
//
// A z-index inside a stacking context cannot escape it, so the menu and the button that opens it were
// trapped underneath the backdrop — except when :focus-within or :hover happened to remove the
// opacity or the card happened not to be hovered, at which point they escaped. The cursor flicking
// between a hand and an arrow was that boundary being crossed. Worse, while trapped the menu itself
// was under the backdrop, so clicking an item would have closed the menu instead of running it.
//
// Listening on the document has none of that: it does not care what is painted where. Anything inside
// an element marked data-menu-anchor is a menu or the button that opens it, and is left alone — the
// button's own toggle handles it. Everything else closes the menu.
(function () {
    'use strict';

    var handler = null;

    window.devtoolboxMenu = {
        // Registers the listener. `owner` is a DotNetObjectReference exposing `method`.
        watch: function (owner, method) {
            if (handler) { return; }

            handler = function (event) {
                if (event.target && event.target.closest && event.target.closest('[data-menu-anchor]')) {
                    return;
                }

                owner.invokeMethodAsync(method);
            };

            // pointerdown, not click: it fires before focus moves and before the click, so the menu is
            // already closing by the time anything else reacts. Capture phase so a handler that stops
            // propagation on the way up cannot leave a menu stranded open.
            document.addEventListener('pointerdown', handler, true);
        },

        stop: function () {
            if (!handler) { return; }

            document.removeEventListener('pointerdown', handler, true);
            handler = null;
        }
    };
})();
