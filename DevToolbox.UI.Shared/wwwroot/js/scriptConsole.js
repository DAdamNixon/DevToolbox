// The Scripts tab's console. It follows new output as it arrives, stops following the moment you
// scroll up to read something, and offers a way back to the bottom when you have.
//
// All of it here rather than in the component: following has to happen after every render that
// added a line, ten a second during a busy run, and a round trip to .NET per render to ask "should
// I scroll?" would be most of the work. A MutationObserver sees the new lines itself.
window.scriptConsole = {
    // How close to the bottom still counts as "at the bottom". A line or so, so a scroll that
    // lands a pixel short does not quietly stop the console from following.
    slack: 24,

    attach: function (body) {
        if (!body || body.dataset.bound === '1') return;
        body.dataset.bound = '1';

        const section = body.closest('.ps-console');
        const jump = section ? section.querySelector('.ps-console-jump') : null;
        let pinned = true;

        const toBottom = function () { body.scrollTop = body.scrollHeight; };
        const setPinned = function (value) {
            pinned = value;
            if (section) section.classList.toggle('is-scrolled-up', !value);
        };

        body.addEventListener('scroll', function () {
            setPinned(body.scrollHeight - body.scrollTop - body.clientHeight <= window.scriptConsole.slack);
        });

        new MutationObserver(function () { if (pinned) toBottom(); })
            .observe(body, { childList: true, subtree: true, characterData: true });

        if (jump) {
            jump.addEventListener('click', function () {
                setPinned(true);
                toBottom();
            });
        }

        body._scriptConsole = { toBottom: toBottom, setPinned: setPinned };
        toBottom();
    },

    // A run has just started: bring the console on screen and follow it from the start, even if
    // the last run had been scrolled up.
    reveal: function (body) {
        if (!body) return;

        const section = body.closest('.ps-console') || body;
        section.scrollIntoView({ block: 'nearest' });

        if (body._scriptConsole) {
            body._scriptConsole.setPinned(true);
            body._scriptConsole.toBottom();
        }
    }
};
