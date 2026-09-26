using DevToolbox.Services.Models;

namespace DevToolbox.UI.Services;

/// <summary>
/// What the sort menus offer, and what each option is called.
/// <para>
/// Shared between the group header's menu and the arrange dialog's dropdown so the two cannot
/// drift — they are the same choice reached two ways, and a group reading "Name A–Z" on its header
/// while the dialog says "Default" would be the dashboard disagreeing with itself.
/// </para>
/// </summary>
public static class CardSortOptions
{
    /// <summary>
    /// The options worth offering for a group.
    /// <para>
    /// <see cref="CardSort.Default"/> means "do not reorder", which for a scanned group is
    /// alphabetical — the scan sorts its own output — so offering it beside <see cref="CardSort.Name"/>
    /// there is offering the same thing twice. It only says something different for a hand-made
    /// group, where it is the order the cards were added in, so that is the only place it appears.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CardSort> For(bool isFromSource) => isFromSource
        ? new[] { CardSort.Name, CardSort.Locations, CardSort.Custom }
        : new[] { CardSort.Default, CardSort.Name, CardSort.Locations, CardSort.Custom };

    /// <summary>
    /// What to call an option. <see cref="CardSort.Default"/> is named for what it actually does,
    /// which depends on where the group came from — and for a scanned group it is alphabetical, so
    /// it reports itself as such rather than as a fourth mystery option.
    /// </summary>
    public static string Label(CardSort value, bool isFromSource) => value switch
    {
        CardSort.Name => "Name A–Z",
        CardSort.Locations => "Most locations",
        CardSort.Custom => "Custom",
        _ => isFromSource ? "Name A–Z" : "As added"
    };

    /// <summary>
    /// Which option a menu should show as chosen. Nothing stored means <see cref="CardSort.Default"/>,
    /// and for a scanned group that is indistinguishable from <see cref="CardSort.Name"/> — so Name
    /// is what gets the tick, because claiming otherwise would be describing a difference the user
    /// cannot see.
    /// </summary>
    public static CardSort Selected(CardSort stored, bool isFromSource) =>
        stored == CardSort.Default && isFromSource ? CardSort.Name : stored;

    public static string Icon(CardSort value) => value switch
    {
        CardSort.Locations => "bi-sort-numeric-down-alt",
        CardSort.Custom => "bi-list-ol",
        CardSort.Name => "bi-sort-alpha-down",
        _ => "bi-arrow-down-up"
    };
}
