/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./Pages/**/*.{razor,html,cshtml}",
    "./Components/**/*.{razor,html,cshtml}",
    "./Shared/**/*.{razor,html,cshtml}",
    "./wwwroot/**/*.{html,js}",
    "./*.razor"
  ],
  theme: {
    extend: {
      // Every colour resolves through a CSS variable declared in
      // wwwroot/css/theme.css, so a theme switch is a variable swap at runtime
      // and needs no rebuild. `<alpha-value>` is the placeholder Tailwind
      // substitutes for opacity modifiers — without it `bg-dark-surface/50`
      // (used by .glass-effect and .search-input) would silently lose its
      // transparency.
      //
      // The names are historical: 'dark-bg' now means "the page background",
      // which is white under the light theme. Renaming them to semantic names
      // would touch every razor file and is left for its own pass.
      colors: {
        'dark-bg': 'rgb(var(--c-bg) / <alpha-value>)',
        'dark-surface': 'rgb(var(--c-surface) / <alpha-value>)',
        'dark-surface-hover': 'rgb(var(--c-surface-hover) / <alpha-value>)',
        'dark-border': 'rgb(var(--c-border) / <alpha-value>)',
        'dark-text': 'rgb(var(--c-text) / <alpha-value>)',
        'dark-text-muted': 'rgb(var(--c-text-muted) / <alpha-value>)',
        'accent-blue': 'rgb(var(--c-accent-blue) / <alpha-value>)',
        'accent-blue-hover': 'rgb(var(--c-accent-blue-hover) / <alpha-value>)',
        'accent-purple': 'rgb(var(--c-accent-purple) / <alpha-value>)',
        'accent-purple-hover': 'rgb(var(--c-accent-purple-hover) / <alpha-value>)',
        'success': 'rgb(var(--c-success) / <alpha-value>)',
        'warning': 'rgb(var(--c-warning) / <alpha-value>)',
        'danger': 'rgb(var(--c-danger) / <alpha-value>)',
      },
      fontFamily: {
        'sans': ['Inter', 'system-ui', 'sans-serif'],
        'mono': ['JetBrains Mono', 'Monaco', 'Consolas', 'monospace'],
      },
      // Glows follow the accent so they stay in step with the theme. The black
      // drop shadows are scaled by --shadow-strength because the same opacity
      // that reads as depth over #1a1a1a reads as grime over white.
      boxShadow: {
        'glow-sm': '0 0 5px rgb(var(--c-accent-blue) / 0.3)',
        'glow-md': '0 0 15px rgb(var(--c-accent-blue) / 0.4)',
        'glow-lg': '0 0 30px rgb(var(--c-accent-blue) / 0.5)',
        'dark-lg': '0 10px 25px -3px rgb(0 0 0 / calc(0.3 * var(--shadow-strength))), 0 4px 6px -2px rgb(0 0 0 / calc(0.2 * var(--shadow-strength)))',
        'dark-xl': '0 20px 25px -5px rgb(0 0 0 / calc(0.4 * var(--shadow-strength))), 0 10px 10px -5px rgb(0 0 0 / calc(0.3 * var(--shadow-strength)))',
      },
      animation: {
        'fade-in': 'fadeIn 0.3s ease-in-out',
        'slide-up': 'slideUp 0.3s ease-out',
        'scale-in': 'scaleIn 0.2s ease-out',
        'glow-pulse': 'glowPulse 2s ease-in-out infinite',
      },
      keyframes: {
        fadeIn: {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        slideUp: {
          '0%': { transform: 'translateY(10px)', opacity: '0' },
          '100%': { transform: 'translateY(0)', opacity: '1' },
        },
        scaleIn: {
          '0%': { transform: 'scale(0.95)', opacity: '0' },
          '100%': { transform: 'scale(1)', opacity: '1' },
        },
        glowPulse: {
          '0%, 100%': { boxShadow: '0 0 5px rgb(var(--c-accent-blue) / 0.3)' },
          '50%': { boxShadow: '0 0 20px rgb(var(--c-accent-blue) / 0.6)' },
        },
      },
    },
  },
}