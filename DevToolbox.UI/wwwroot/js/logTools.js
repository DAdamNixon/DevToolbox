// Column resizing for the Log Viewer results table.
window.logTools = {
    initColumnResize: function (tableId) {
        const table = document.getElementById(tableId);
        if (!table) return;

        const colgroup = table.querySelector('colgroup');
        const cols = colgroup ? colgroup.children : null;
        const headers = table.querySelectorAll('thead th');
        headers.forEach(function (th, index) {
            const handle = th.querySelector('.col-resizer');
            if (!handle || handle.dataset.bound === '1') return;
            handle.dataset.bound = '1';

            // Under table-layout:fixed the <col> width governs the column, so resize the col.
            const targetCol = cols && cols[index] ? cols[index] : null;
            let startX = 0;
            let startWidth = 0;

            const onMouseMove = function (e) {
                const newWidth = Math.max(60, startWidth + (e.clientX - startX));
                if (targetCol) targetCol.style.width = newWidth + 'px';
                th.style.width = newWidth + 'px';
            };

            const onMouseUp = function () {
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);
                document.body.style.userSelect = '';
            };

            handle.addEventListener('mousedown', function (e) {
                e.preventDefault();
                e.stopPropagation();
                startX = e.clientX;
                startWidth = th.offsetWidth;
                document.body.style.userSelect = 'none';
                document.addEventListener('mousemove', onMouseMove);
                document.addEventListener('mouseup', onMouseUp);
            });

            // Prevent a resize interaction from also triggering the column sort click.
            handle.addEventListener('click', function (e) { e.stopPropagation(); });
        });
    }
};

// The visible viewport, so the row context menu can be kept on screen. MouseEventArgs
// carries the pointer position but nothing about how much room is left around it.
window.logTools.viewportSize = function () {
    return { width: window.innerWidth, height: window.innerHeight };
};

// Keyboard shortcuts for the Log Viewer: Escape closes the full-screen results,
// Ctrl+F raises the filters over them.
//
// A document listener rather than @onkeydown on the card, for the same reason
// menu.js listens on the document: the keys have to work wherever focus happens
// to be — the page-number box, a filter field, nothing at all — and a handler
// bound to one element only fires when that element has focus.
window.logTools.watchKeys = function (owner, escapeMethod, findMethod) {
    if (window.logTools._keyHandler) { return; }

    window.logTools._keyHandler = function (event) {
        if (event.key === 'Escape') {
            owner.invokeMethodAsync(escapeMethod);
            return;
        }

        // Ctrl+F means find, so the filters are what it should reach for. The
        // default has to be swallowed or WebView2 opens its own find bar on top
        // of the panel. Alt excluded so AltGr layouts still type their character.
        const key = (event.key || '').toLowerCase();
        if (key === 'f' && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey) {
            event.preventDefault();
            owner.invokeMethodAsync(findMethod);
        }
    };

    document.addEventListener('keydown', window.logTools._keyHandler);
};

window.logTools.stopKeys = function () {
    if (!window.logTools._keyHandler) { return; }

    document.removeEventListener('keydown', window.logTools._keyHandler);
    window.logTools._keyHandler = null;
};

// Opens the suggestion list of a datalist input.
//
// Needed because the Log Viewer hides Chromium's own datalist arrow: that arrow is
// drawn inside the input's right padding, so reserving room there for the clear
// button left it adrift in the middle of the field. showPicker() is the only way to
// ask for the list, since clicking an <input list> does not reliably open one.
window.logTools.showFileList = function (inputId) {
    const input = document.getElementById(inputId);
    if (!input) { return; }

    input.focus();

    try {
        input.showPicker();
    } catch (e) {
        // Older engine, or the call was not treated as a user gesture. Focus alone
        // still brings the list up on the first keystroke or on Down.
    }
};

// Put the caret in the first filter field, so Ctrl+F lands on something typable
// rather than only making the panel appear.
//
// Caret at the end rather than a select-all: unlike a browser's find box this
// holds a filter the user built up, and one keystroke should not wipe it.
window.logTools.focusFilter = function () {
    const card = document.getElementById('logFilterCard');
    if (!card) { return; }

    // The keyword boxes carry no type attribute; the advanced-mode box is a
    // textarea. Either way the mode checkbox above them is skipped.
    const field = card.querySelector('textarea, input[type="text"], input:not([type])');
    if (!field) { return; }

    field.focus();

    const end = field.value ? field.value.length : 0;
    try { field.setSelectionRange(end, end); } catch { /* not a text field */ }

    // Only does anything when the card is in page flow: raised over the results
    // it is position:fixed and already on screen.
    card.scrollIntoView({ block: 'nearest' });
};
