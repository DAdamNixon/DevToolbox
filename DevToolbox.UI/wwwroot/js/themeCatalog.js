// The list of themes, and the tuning for each animation. Data only — no logic,
// nothing to run. theme.js and themeEffects.js both read it.
//
// This duplicates DevToolbox.Services/Models/ThemeCatalog.cs, and has to: the
// palette must be on <html> before first paint, which happens long before any
// .NET code has read a config file, so the boot script cannot ask C# what the
// themes are. ThemeCatalogTests compares the two and fails if they drift, so the
// copy is checked rather than merely hoped for.
//
// The test parses this array with a regex. Keep one theme per line and keep the
// field order — id, season, effect. Labels are deliberately absent: only the
// Settings page renders a name and that comes from the C# side.
//
//   season   'MM-DD..MM-DD', or null for a theme that is always offered.
//            A window may wrap the new year (Winter is '12-01..02-29'); the
//            comparison is on month and day only, so there is no year to get
//            wrong and 02-29 simply includes the 28th every year.
//   effect   the id of an animation in `effects` below, or null for a still
//            theme. Standard themes never animate.
window.devtoolboxThemes = {
    themes: [
        { id: 'system',           season: null,           effect: null },
        { id: 'dark',             season: null,           effect: null },
        { id: 'light',            season: null,           effect: null },
        { id: 'fall',             season: '09-01..11-30', effect: 'leaves' },
        { id: 'halloween',        season: '10-01..10-31', effect: 'bats' },
        { id: 'thanksgiving',     season: '11-01..11-30', effect: 'leaves' },
        { id: 'winter',           season: '12-01..02-29', effect: 'snow' },
        { id: 'christmas',        season: '12-01..12-31', effect: 'snow' },
        { id: 'better-christmas', season: '12-01..12-31', effect: 'snow' }
    ],

    // Per-effect tuning. `count` is the whole cost of the feature — every
    // particle is one absolutely-positioned span running a compositor-only
    // transform, so these numbers are the only thing standing between a bit of
    // weather and a warm laptop. Sizes are px; durations are seconds for one
    // top-to-bottom pass; drift is the horizontal travel over that pass, in vw.
    effects: {
        snow:   { count: 55, minSize: 8,  maxSize: 20, minDuration: 9, maxDuration: 20, maxDrift: 12 },
        leaves: { count: 26, minSize: 14, maxSize: 30, minDuration: 8, maxDuration: 17, maxDrift: 22 },
        bats:   { count: 7,  minSize: 16, maxSize: 30, minDuration: 7, maxDuration: 14, maxDrift: 10 }
    }
};
