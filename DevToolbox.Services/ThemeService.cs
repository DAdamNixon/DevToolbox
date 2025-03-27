using System;
using System.Threading.Tasks;

namespace DevToolbox.Services
{
    /// <summary>
    /// Service for managing application theme (dark/light mode)
    /// </summary>
    public class ThemeService
    {
        /// <summary>
        /// Event raised when the theme changes
        /// </summary>
        public event EventHandler<bool>? ThemeChanged;
        
        /// <summary>
        /// Gets or sets whether dark mode is enabled
        /// </summary>
        public bool IsDarkModeEnabled { get; private set; }
        
        /// <summary>
        /// Initializes the theme service
        /// </summary>
        public ThemeService()
        {
            // Default value, will be updated on app start
            IsDarkModeEnabled = false;
        }
        
        /// <summary>
        /// Sets the initial theme state
        /// </summary>
        /// <param name="isDarkMode">Whether dark mode is enabled</param>
        public void Initialize(bool isDarkMode)
        {
            IsDarkModeEnabled = isDarkMode;
        }
        
        /// <summary>
        /// Toggles the current theme
        /// </summary>
        /// <returns>The new theme state (true for dark mode, false for light mode)</returns>
        public Task<bool> ToggleThemeAsync()
        {
            IsDarkModeEnabled = !IsDarkModeEnabled;
            ThemeChanged?.Invoke(this, IsDarkModeEnabled);
            return Task.FromResult(IsDarkModeEnabled);
        }
        
        /// <summary>
        /// Sets the theme explicitly
        /// </summary>
        /// <param name="isDarkMode">Whether to enable dark mode</param>
        /// <returns>The new theme state</returns>
        public Task<bool> SetThemeAsync(bool isDarkMode)
        {
            if (IsDarkModeEnabled != isDarkMode)
            {
                IsDarkModeEnabled = isDarkMode;
                ThemeChanged?.Invoke(this, IsDarkModeEnabled);
            }
            return Task.FromResult(IsDarkModeEnabled);
        }
    }
} 