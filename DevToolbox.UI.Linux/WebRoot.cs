using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace DevToolbox.UI.Linux;

/// <summary>
/// The files the Photino window serves: this host's wwwroot (index.html) and DevToolbox.UI.Shared's
/// (every stylesheet, script and image).
/// <para>
/// A publish copies both into one wwwroot folder beside the executable. A build does not: the
/// shared library's files stay in its own project, and devtoolbox.staticwebassets.runtime.json says
/// where. Kestrel reads that manifest by itself, and the Windows window's BlazorWebView does too;
/// PhotinoX only reads a folder. So this asks ASP.NET's own loader to lay the manifest over the
/// folder, as Kestrel does. Beside a publish there is no manifest, and it leaves the folder as it is.
/// </para>
/// </summary>
internal static class WebRoot
{
    public static IFileProvider Files()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var environment = new Environment
        {
            ApplicationName = typeof(WebRoot).Assembly.GetName().Name!,
            ContentRootPath = AppContext.BaseDirectory,
            ContentRootFileProvider = new PhysicalFileProvider(AppContext.BaseDirectory),
            WebRootPath = root,
            WebRootFileProvider = Directory.Exists(root) ? new PhysicalFileProvider(root) : new NullFileProvider(),
        };

        StaticWebAssetsLoader.UseStaticWebAssets(environment, new ConfigurationBuilder().Build());
        return environment.WebRootFileProvider;
    }

    /// <summary>What the loader needs to know, and nothing else of a web host.</summary>
    private sealed class Environment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public required string ApplicationName { get; set; }
        public required string ContentRootPath { get; set; }
        public required IFileProvider ContentRootFileProvider { get; set; }
        public required string WebRootPath { get; set; }
        public required IFileProvider WebRootFileProvider { get; set; }
    }
}
