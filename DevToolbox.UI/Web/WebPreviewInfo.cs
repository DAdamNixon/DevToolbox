namespace DevToolbox.UI.Web;

/// <summary>
/// Where the browser view is listening, published into the container so pages can
/// read it.
/// <para>
/// A holder rather than the host itself, because of the order things happen in:
/// <see cref="WebPreviewHost"/> builds the container, so it cannot be registered
/// inside the container it is still constructing. This is registered up front and
/// filled in once the port is actually bound.
/// </para>
/// </summary>
public sealed class WebPreviewInfo
{
    /// <summary>The address to open, or null when the server is not listening.</summary>
    public string? Url { get; internal set; }

    /// <summary>Why it is not listening, when <see cref="Url"/> is null.</summary>
    public string? Error { get; internal set; }

    public bool IsRunning => Url is not null;
}
