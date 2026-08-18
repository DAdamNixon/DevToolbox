namespace DevToolbox.UI.Services;

/// <summary>
/// Which dropdown menu, anywhere in the application, is currently open — and there is only ever one.
/// <para>
/// Every menu used to own a private <c>bool</c>, which meant nothing could close a menu except the
/// component holding it. There was no click-outside dismissal anywhere: a menu stayed up until you
/// clicked its own button again or picked an item, and since <c>.ws-menu</c> is absolutely positioned
/// it hangs over the cards below whenever it is taller than the card it belongs to. Clicking another
/// card did nothing, so it read as stuck. Both halves of that are fixed here: one owner means opening
/// any menu closes every other, and a single backdrop in <c>MainLayout</c> means clicking anywhere
/// else closes the one that is open.
/// </para>
/// </summary>
public sealed class MenuStateService
{
    private string? _openMenuId;

    /// <summary>
    /// Raised whenever the open menu changes. Every component showing a menu has to listen, because
    /// the click that closes its menu usually happens somewhere else entirely.
    /// </summary>
    public event Action? Changed;

    /// <summary>Whether any menu is open — what the backdrop keys off.</summary>
    public bool AnyOpen => _openMenuId is not null;

    public bool IsOpen(string menuId) => _openMenuId is not null && _openMenuId == menuId;

    /// <summary>Opens this menu, closing whatever was open; or closes it if it was the open one.</summary>
    public void Toggle(string menuId)
    {
        if (string.IsNullOrEmpty(menuId)) return;

        _openMenuId = IsOpen(menuId) ? null : menuId;
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes whatever is open. Silent when nothing is, so the many callers that close a menu on
    /// their way to doing something else do not each cost a re-render of every card on the page.
    /// </summary>
    public void Close()
    {
        if (_openMenuId is null) return;

        _openMenuId = null;
        Changed?.Invoke();
    }
}
