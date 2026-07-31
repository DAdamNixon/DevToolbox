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
