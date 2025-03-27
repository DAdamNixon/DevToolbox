using Microsoft.JSInterop;
using System.Threading.Tasks;

namespace DevToolbox.UI.Services
{
    /// <summary>
    /// JavaScript interop service for theme management
    /// </summary>
    public class ThemeJsInterop
    {
        private readonly IJSRuntime _jsRuntime;

        public ThemeJsInterop(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        /// <summary>
        /// Gets the current dark mode state from the browser
        /// </summary>
        /// <returns>True if dark mode is enabled, false otherwise</returns>
        public async Task<bool> IsDarkModeEnabledAsync()
        {
            return await _jsRuntime.InvokeAsync<bool>("isDarkModeEnabled");
        }

        /// <summary>
        /// Toggles dark mode in the browser
        /// </summary>
        /// <param name="enabled">Whether to enable dark mode</param>
        /// <returns>True if successful</returns>
        public async Task<bool> ToggleDarkModeAsync(bool enabled)
        {
            return await _jsRuntime.InvokeAsync<bool>("toggleDarkMode", enabled);
        }
    }
} 