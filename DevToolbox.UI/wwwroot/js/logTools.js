// Column resizing and clipboard helpers for the Log Viewer results table.
window.logTools = {
    initColumnResize: function (tableId) {
        const table = document.getElementById(tableId);
        if (!table) return;

        const headers = table.querySelectorAll('thead th');
        headers.forEach(function (th) {
            const handle = th.querySelector('.col-resizer');
            if (!handle || handle.dataset.bound === '1') return;
            handle.dataset.bound = '1';

            let startX = 0;
            let startWidth = 0;

            const onMouseMove = function (e) {
                const newWidth = Math.max(60, startWidth + (e.clientX - startX));
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
    },

    copyToClipboard: function (text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            return navigator.clipboard.writeText(text);
        }
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); } finally { document.body.removeChild(ta); }
        return Promise.resolve();
    }
};
