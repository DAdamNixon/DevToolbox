// Makes the group cards' drag handles behave like real drag sources.
//
// Blazor's @ondragstart hands C# a DragEventArgs, which is a copy of the event with no
// dataTransfer on it — so the two things a drag needs doing at dragstart time cannot be done
// from a component at all:
//
//   setData      Chromium will start a drag without a payload; Firefox will not, and a
//                dragstart that sets nothing is a drag that never produces a drop event.
//   setDragImage without it the ghost under the cursor is the grip glyph alone, a 10px dot,
//                which gives no clue what is being moved.
//
// A single document listener covers every handle, present and future, with no per-card
// registration and nothing to dispose. Anything marked data-drag-handle is a handle; the
// data-drag-card it sits inside is what the ghost is made of.
(function () {
    'use strict';

    document.addEventListener('dragstart', function (event) {
        var target = event.target;
        if (!target || !target.closest) { return; }

        var handle = target.closest('[data-drag-handle]');
        if (!handle || !event.dataTransfer) { return; }

        event.dataTransfer.effectAllowed = 'move';

        try {
            event.dataTransfer.setData('text/plain', handle.getAttribute('data-drag-handle') || '');
        } catch (err) {
            // Some hosts lock dataTransfer down. Chromium drags fine without it.
        }

        var card = handle.closest('[data-drag-card]');
        if (card && event.dataTransfer.setDragImage) {
            // Offset roughly onto the grip, so the ghost sits under the cursor where it was
            // picked up rather than centred on a card that may be a thousand pixels wide.
            event.dataTransfer.setDragImage(card, 24, 20);
        }
    });

    // ── keeping a dropdown inside the window ────────────────────────────────────────────
    //
    // .ws-menu is absolutely positioned against whatever opened it, and no single alignment
    // is right everywhere: right: 0 suits a ⋮ at a card's top-right, left: 0 suits a chip at
    // a card's bottom-left, and either one runs off the window when the anchor is near that
    // edge. The card grid puts anchors hard against both edges by design.
    //
    // So the CSS picks the alignment that is right most of the time and this nudges whatever
    // is still hanging over an edge back inside. A translate, not a left/top override, so it
    // cannot fight the CSS that positioned the menu in the first place.
    //
    // Driven by a MutationObserver rather than from Blazor: the menu is rendered by three
    // different components and this way none of them has to know. Only ever one menu is open
    // — MenuStateService guarantees it — so the scan below stops at the first hit.

    var EDGE = 8;

    function fit(menu) {
        // Cleared first so the measurement is of where the CSS put it, not where a previous
        // call moved it.
        menu.style.transform = '';
        menu.style.maxHeight = '';

        var rect = menu.getBoundingClientRect();

        // A menu with a long Run Script list can outgrow a short window. Scrolling inside it
        // beats being dragged off both ends at once.
        var tallest = window.innerHeight - EDGE * 2;
        if (rect.height > tallest) {
            menu.style.maxHeight = tallest + 'px';
            rect = menu.getBoundingClientRect();
        }

        // Right/bottom first, then left/top, so on a window too small for the menu the top-left
        // corner is the one that stays reachable.
        var dx = 0;
        if (rect.right > window.innerWidth - EDGE) { dx = window.innerWidth - EDGE - rect.right; }
        if (rect.left + dx < EDGE) { dx = EDGE - rect.left; }

        var dy = 0;
        if (rect.bottom > window.innerHeight - EDGE) { dy = window.innerHeight - EDGE - rect.bottom; }
        if (rect.top + dy < EDGE) { dy = EDGE - rect.top; }

        if (dx || dy) {
            menu.style.transform = 'translate(' + Math.round(dx) + 'px, ' + Math.round(dy) + 'px)';
        }
    }

    function fitAdded(node) {
        if (node.nodeType !== 1) { return; }

        if (node.classList && node.classList.contains('ws-menu')) {
            fit(node);
            return;
        }

        // The menu is normally inserted as its own node, straight from an @if. This covers the
        // case where a card is rebuilt wholesale with one already open.
        if (node.firstElementChild) {
            var inner = node.querySelector('.ws-menu');
            if (inner) { fit(inner); }
        }
    }

    // documentElement, not body: this file is loaded from <head>, where body does not exist yet.
    new MutationObserver(function (records) {
        for (var i = 0; i < records.length; i++) {
            var added = records[i].addedNodes;
            for (var j = 0; j < added.length; j++) {
                fitAdded(added[j]);
            }
        }
    }).observe(document.documentElement, { childList: true, subtree: true });

    // A resize while a menu is open would otherwise leave it wherever it was.
    window.addEventListener('resize', function () {
        var open = document.querySelector('.ws-menu');
        if (open) { fit(open); }
    });
})();
