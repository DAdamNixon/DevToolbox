let dotNetCallback = null;

window.registerSearchShortcut = (searchInput, dotNetHelper) => {
    dotNetCallback = dotNetHelper;

    document.addEventListener('keydown', (e) => {
        // This listener is never removed, so navigating away from the dashboard
        // leaves it holding a box that is no longer in the document. Bailing out
        // on that lets the page you are actually on have Ctrl+F — the Log Viewer
        // uses it for its filters — instead of it being swallowed here.
        if (!searchInput || !searchInput.isConnected) { return; }

        if ((e.ctrlKey || e.metaKey) && e.key === 'f') {
            e.preventDefault();
            searchInput.focus();
            if (searchInput.value !== '') {
                searchInput.value = '';
                document.dispatchEvent(new CustomEvent('searchCleared', { detail: '' }));
            }
        }
    });
};

// Listen for the custom event
document.addEventListener('searchCleared', () => {
    if (dotNetCallback) {
        dotNetCallback.invokeMethodAsync('HandleSearchCleared');
    }
});