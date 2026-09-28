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
// Listening on the document has none of that: it does not care what is painted where. Three things
// are left alone — a right-click, a click inside an open menu, and a click on the control that opens
// one — and everything else closes the menu.
//
// The right-button check is what makes right-click menus possible at all. pointerdown fires before
// contextmenu, so without it every right-click closed the menu its own contextmenu handler was
// about to open. That used to be worked around by marking whole cards data-menu-anchor, which cured
// the symptom and caused a worse one: the attribute means "clicking here does not dismiss", and a
// card is most of the screen — so with the group cards and the workspace cards both marked, there
// was nearly nowhere left to click to get rid of a menu. The button number is the actual
// distinction, so it is the thing to test.
(function () {
    'use strict';

    var handler = null;

    window.devtoolboxMenu = {
        // Registers the listener. `owner` is a DotNetObjectReference exposing `method`.
        watch: function (owner, method) {
            if (handler) { return; }

            handler = function (event) {
                // A right-click is opening a menu of its own; its contextmenu handler owns it.
                if (event.button === 2) { return; }

                var target = event.target;
                if (!target || !target.closest) {
                    owner.invokeMethodAsync(method);
                    return;
                }

                // Inside an open menu: the item's own handler owns the click. Checked explicitly
                // rather than relying on the menu sitting inside a marked anchor, because a menu
                // positioned at the cursor is position: fixed and only incidentally a descendant.
                if (target.closest('.ws-menu')) { return; }

                // On a menu's own toggle — the ⋮, the sort button, a quick-open chip. Its click
                // toggles the menu, and closing it here first would fight that.
                if (target.closest('[data-menu-anchor]')) { return; }

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
