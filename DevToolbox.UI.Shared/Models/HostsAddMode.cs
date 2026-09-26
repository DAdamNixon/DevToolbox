namespace DevToolbox.UI.Models;

/// <summary>Which of the three additive shapes the add dialog is currently collecting.</summary>
public enum HostsAddMode
{
    /// <summary>A whole new group and its options.</summary>
    Group,

    /// <summary>One new option inside an existing group.</summary>
    Option,

    /// <summary>
    /// A new option seeded from one that already exists — same hostnames, usually a different
    /// address. Behaves exactly like <see cref="Option"/> once it has been filled in; the only
    /// difference is where the form starts.
    /// </summary>
    Copy,
}
