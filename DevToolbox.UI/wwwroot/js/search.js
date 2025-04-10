let dotNetCallback = null;

window.registerSearchShortcut = (searchInput, dotNetHelper) => {
    dotNetCallback = dotNetHelper;

    document.addEventListener('keydown', (e) => {
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