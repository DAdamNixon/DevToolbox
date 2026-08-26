# Dashboard (Projects tab)

How the Projects tab builds its cards, and the two YAML files that customise it.

## Where the cards come from

Two independent sources feed one list of groups.

**Saved groups** — hand-managed, persisted.

```
Config/workspaceGroups.yaml
  -> YamlStorageService.LoadAsync<List<WorkspaceGroup>>("workspaceGroups")
  -> WorkspaceService.GetWorkspaceGroupsAsync()
  -> WorkspaceGroup -> Workspace -> WorkspaceLocation
```

**Scanned groups** — discovered on disk, virtual.

```
Config/workspaceSources.yaml
  -> WorkspaceSourceService.GetGroupsAsync()
  -> globs each source's folder, folds the results into the same model
```

`Index.razor` concatenates the two, runs them through `ViewModelFactory`, and renders
one `WorkspaceGroupCard` each, which renders a `WorkspaceCard` per workspace, which
renders a row per location.

```
Index.razor
└── WorkspaceGroupCard      group header + grid
    └── WorkspaceCard       one project
        └── location row    one checkout / branch / entry point
```

Between the two sources and the view models sits `IDashboardLayoutService`, which decides
what order the groups come in and which cards are pinned to the front of each — see
[dashboardLayout.yaml](#config-dashboardlayoutyaml). It reads names, not ids, which is the
only way one file can arrange both halves.

Scanned groups are never written back. `SaveWorkspaces()` only ever passes
`WorkspaceService.WorkspaceGroups` (the saved ones) to `SaveWorkspaceGroupsAsync`, so a
rescan cannot corrupt hand-made entries and hand-editing cannot fight the scanner. The
cards carry `IsFromSource`, and the UI swaps their edit actions for *Rescan Source* and
*Open Source Folder*.

Storage for everything below is `%LOCALAPPDATA%\DevToolbox\Config\`:

| File | Purpose |
|---|---|
| `workspaceGroups.yaml` | Hand-added groups, workspaces and locations |
| `workspaceSources.yaml` | Folders scanned for projects |
| `openHandlers.yaml` | Which program opens which kind of file |
| `dashboardIcons.yaml` | Card icons and colours |
| `dashboardLayout.yaml` | Group order, pinned cards, search aliases |

## Config: workspaceSources.yaml

Each entry is a folder to scan. Nothing about a machine's layout lives in the app —
paths, patterns and naming conventions are all declared here.

```yaml
sources:
- name: VS Code Workspaces
  enabled: true
  path: C:\TFS\Workspaces         # %ENVVARS% are expanded
  pattern: '*.code-workspace'
  scan: Files                     # Files | Directories
  recursive: false
  group: VS Code                  # dashboard group; defaults to `name`
  icon: bi-window-stack
  color: '#06b6d4'
  nameRegex: '^(?<location>dev|demo)-(?<workspace>.+)$'
  defaultLocationName: workspace
  descriptionFrom:
  - settings.description
  - folders
```

| Key | Effect |
|---|---|
| `pattern` | Glob applied inside `path`. `*.sln`, `*.code-workspace`, `*` … |
| `scan` | `Files` collects matching files; `Directories` collects matching subfolders (one card per repo folder). |
| `nameRegex` | Optional. Named groups `workspace` and `location` fold several entries into one card. Without it, every entry is its own card. |
| `defaultLocationName` | Location label when the regex supplies none. |
| `descriptionFrom` | Dotted JSON paths tried in order for a one-line subtitle, read only from JSON/JSONC entries. A path landing on an array yields a count, so `folders` renders as `16 folders`. |
| `openWith` | Optional `CustomOpenOption` overriding the Open button **for this source only**. Usually unnecessary — set the program by extension in `openHandlers.yaml` instead, so hand-added cards behave the same. |

`nameRegex` is what turns

```
dev-checkout.code-workspace
demo-checkout.code-workspace
```

into one **checkout** card holding a **dev** and a **demo** location, matching how
hand-added solutions are already organised.

### Scan Folders, and the live preview

Sources can also be edited in the app: **Projects → Scan Folders**. The dialog is
master-detail — sources down the left, one editor on the right — and under the editor is
a **live preview** of the cards that source would add, recomputed 250ms after every edit.

```
IWorkspaceSourceService.PreviewAsync(source, ct)  ->  SourcePreview
  ResolvedPath / PathExists      the folder, with %ENVVARS% expanded
  EntriesFound / Truncated       what the pattern matched on disk
  RegexError                     why nameRegex was ignored, when it was
  Workspaces[].Locations[]       the cards, exactly as the dashboard would show them
  Unmatched                      entries nameRegex missed, which fall back to one card each
```

The preview is not a second implementation of the scan. `WorkspaceSourceService.Collect`
enumerates and splits names, `Fold` turns entries into workspaces and locations, and both
the real scan and the preview call both — so a preview that looks right *is* right. There
is a test that asserts the two agree (`SourcePreviewTests`).

Three things the preview makes visible that were previously invisible until you saved and
squinted at the cards:

- **A `nameRegex` that matches nothing.** The scan does not fail on one, it falls back to
  one card per entry — which looks exactly like a regex that worked. Unmatched entries are
  now listed and flagged in place rather than hidden, because a preview full of fallbacks
  is the signal that the regex is wrong.
- **A `nameRegex` that does not compile.** Also silently ignored before; now named, with
  the .NET regex error.
- **Wrong folder vs. wrong pattern.** `PathExists` separates them, so "nothing here"
  distinguishes a typo in the path from a typo in the glob.

Two bounds on the preview, both because it runs on a keystroke: it stops at 200 entries
(and says `Truncated`), and it runs on a thread pool thread so a network share that has
gone quiet cannot freeze the dialog describing it. Each edit cancels the previous attempt,
so typing a path scans once rather than once per character — and a slow scan of an
abandoned path cannot land on top of the current one.

Nothing is written until **Save and rescan**; closing discards the edits. Saving rewrites
the file, which drops any comments in it.

## Config: openHandlers.yaml

Which program opens which kind of file. The Open button picks, in order:

1. the `openWith` on the workspace source the card came from (scanned cards only)
2. the first handler in `openHandlers.yaml` whose `match` fits
3. the Windows file association

```yaml
handlers:
- match: '*.code-workspace'      # glob on the file name
  name: VS Code
  type: Executable
  executablePath: code

- match: '*.sln'
  name: Visual Studio
  type: Executable
  executableFrom:
    command: '%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe'
    arguments: -latest -products * -property productPath
  executablePath: devenv         # fallback if the locator finds nothing
```

A `match` containing a path separator is tested against the whole path instead of the
file name, which scopes a rule to one tree.

A default `openHandlers.yaml` **ships with the application**, in `ConfigDefaults` beside the
executable, and is copied into the config folder on first run — never overwriting a file that
is already there. Without it a fresh machine has no handlers at all, so every Open falls to
step 3, and step 3 is wrong for exactly the files the dashboard is made of: on a machine with
SQL Server Management Studio installed, the association for `.sln` is SSMS, which opens the
solution as a text query.

The shipped default claims only `*.sln`, `*.slnf` and `*.code-workspace` — the extensions the
association actively gets wrong. `.log` and `.txt` are left to Windows on purpose: whatever it
picks does open them, and naming an editor the machine may not have would turn a working Open
into an error. `DevToolbox.Services/ConfigDefaults/openHandlers.yaml` carries commented rules
for those two, ready to uncomment.

### Why the file association is the last resort

It looks like the obvious default and it is not trustworthy. Every one of these failed
silently, in different ways:

- **`.code-workspace`** had no handler registered at all — VS Code only claims it when
  the installer's "register as an editor for supported file types" box was ticked.
  `Process.Start` then throws, or pops the *How do you want to open this file?* picker.
- **`.sln`** *is* registered, to `VSLauncher.exe`, which picks a Visual Studio by
  reading the solution's `# Visual Studio Version` header. On a machine with more than
  one VS it can resolve to the wrong one — here an old VS 2019 that cannot open a
  `Version 18` solution. The launcher exits without a word.
- **`.sln`**, on a machine with SSMS, is not registered to `VSLauncher.exe` at all —
  SQL Server Management Studio claims the extension during its install and opens the
  solution as a text query. Nothing errors: the wrong program starts and looks like it
  worked. This is the failure the shipped default exists to prevent.

Naming the program removes the guesswork in every case.

### Resolving the executable

`executablePath` is resolved the way a shell would, in order:

1. an explicit path, used as-is
2. a bare name searched across `PATH` × `PATHEXT` — so `code` lands on `code.cmd`
3. the **App Paths** registry, the mechanism Start ▸ Run uses, with the target verified
   to exist

Step 2 matters because `Process.Start` with `UseShellExecute = false` does **not** apply
PATHEXT: plain `code` fails even though it works in a terminal. A resolved `.cmd`/`.bat`
is run through `cmd /c` with `CreateNoWindow`, since CreateProcess cannot execute a
batch file directly.

`executableFrom` runs a command and takes the first line of its stdout as the path,
which beats all of the above. It exists because App Paths is not authoritative — on this
machine `App Paths\devenv.exe` points at VS 2019 while the current install is VS 2026,
so only `vswhere` gives the right answer, and it keeps giving the right answer after an
upgrade moves the install. Results are cached per session, so the Open button does not
spawn a discovery process on every click.

Leave `arguments` unset and the path is appended as one correctly-quoted argument via
`ArgumentList`. Set it (e.g. `-n "{0}"`) and `{0}` is substituted — by `Replace`, not
`string.Format`, so a stray brace in the template cannot throw.

`type: Command` runs the string through PowerShell using `-EncodedCommand`, which
sidesteps quoting entirely. It previously interpolated into `-Command "…"` and broke on
any command containing a double quote — that is, any command taking a path.

Failures are no longer silent. Every open returns an `OpenResult`, and the dashboard
shows the reason in a dismissible banner instead of writing it to a console nobody
sees.

## Config: dashboardIcons.yaml

Resolution order for any card:

```
overrides[name]  ->  first matching rule  ->  the source's own icon  ->  defaults
```

```yaml
defaults:
  group: bi-folder2
  workspace: bi-code-square
  location: bi-folder
  groupColor: '#3b82f6'

overrides:                       # exact name, beats every rule
  groups:
    ElliottElectric: { icon: bi-globe-americas, color: '#3b82f6' }
  locations:
    dev:  { icon: bi-hammer,     color: '#f59e0b' }
    demo: { icon: bi-eyeglasses, color: '#14b8a6' }

rules:                           # first match wins
- match: checkout                # substring unless `regex: true`
  scope: Workspace               # Any | Group | Workspace | Location
  icon: bi-cart3
  color: '#f59e0b'
- match: '^EES\.'
  regex: true
  scope: Workspace
  icon: bi-box-seam

catalog: []                      # picker's icon list; empty = built-in set
palette: []                      # picker's colour list; empty = built-in set
```

Icon names are [Bootstrap Icons](https://icons.getbootstrap.com). **Change Icon** in a
card's menu writes to `overrides` (and rewrites the file, dropping comments); *Reset to
default* removes the override and hands the card back to the rules.

## Config: dashboardLayout.yaml

How the tab is *arranged*, as opposed to what is on it: the group order, which cards are
pinned, and the aliases each card answers to in the search box.

```yaml
groupOrder:                      # drag a group's grip to rewrite this
- ElliottElectric
- VS Code
- NuGetPackages

pinned:                          # group name -> pinned workspace names
  ElliottElectric:
  - Kiosk

aliases:
  groups:
    ElliottElectric: [ee]
  workspaces:
    InvoiceApproval: [invapp, billing]
```

### Why this is a separate file, keyed on names

Half of what the dashboard shows is scanned from disk and rebuilt on every rescan, with a
fresh negative id each time (see `WorkspaceSourceService.Scan`). So there is nowhere in
`workspaceGroups.yaml` to record that a *scanned* group sits second or that a *scanned*
card is pinned, and an id-keyed arrangement would survive exactly one scan.

Keying on display name — the same identity `dashboardIcons.yaml` uses for its overrides —
covers both halves with one file, survives a rescan, and can be hand-edited without
looking anything up. Every lookup is case-insensitive, because a hand-edited file will not
match the app's casing and the same name is the same card either way.

Consequences worth knowing:

- **Ordering is a stable sort on rank**, with unlisted groups at `int.MaxValue`. A group
  added or newly scanned since the last drag lands at the bottom rather than silently
  first, and two groups sharing a name — a saved one and a scanned one — keep their
  relative order instead of swapping about between renders.
- **A drag persists the whole visible order**, not just the two names involved. The stored
  list is often empty (nothing dragged yet) or stale (something scanned since), and
  inserting into it would move four other cards as a side effect of moving one.
- **Pins are a stable sort on a bool**, so pinning promotes one card and leaves every
  other exactly where it was. `ViewModelFactory.CreateWorkspaceGroupViewModel` takes the
  ordered sequence as a parameter rather than sorting `group.Workspaces` — that list is
  what gets written back to `workspaceGroups.yaml`, and pinning a card is not a reason to
  rewrite the file.
- **Empty entries are removed rather than written blank**, so unpinning the last card in a
  group drops the group's key instead of leaving `Group: []` behind for ever.

## Search

The box filters group cards and workspace cards as you type. A card is a hit three ways,
checked in this order (`WorkspaceSearch`):

1. **Substring** on the name, case-insensitive. Always tried first.
2. **An alias** from `dashboardLayout.yaml`, by substring or abbreviation. A prefix of an
   alias is enough — an alias is a word the user chose and they should not have to finish
   typing it.
3. **Abbreviation** of the name (`FuzzyMatch`), which is what makes `persman` find
   `PersonnelManagement` and `eesws` find `EESWebShares` with nothing configured.

A group whose own name or alias matches keeps *all* of its cards: typing a group's
shorthand is a request for the group, not for whichever cards inside it happen to be
spelled like it.

### Where a substring is allowed to start

Both the name and the path searches require the match to **start a word** in some cases,
using the same `FuzzyMatch.IsWordBoundary` the abbreviation matcher uses — one definition
of "word start" for the whole box.

| | Mid-word substring | Why |
|---|---|---|
| Path | never | |
| Name, query < 4 chars | never | |
| Name, query ≥ 4 chars | allowed | |

Both rules come from the same report: **`ai` matched 47 of 457 cards.** All 47 NuGet
packages live under a `\Main\` branch folder, so `ai` hit every one of them through the
middle of the word "Main" — and on the name side it hit Em**ai**l, Tr**ai**ning,
M**ai**nt, W**ai**vers and Ch**ai**ning. None of that is what anyone typing `ai` wants.
Anchoring on a word start takes it to 8 cards, all of them AIM- or AI-related.

- **Paths, always.** What people search a path for is a folder, a branch, a file name or a
  pasted path — all of which start a word. The middles are shared by every sibling, which
  is exactly why matching them is useless. Abbreviation matching on a path is refused for
  the same reason, twice over: `cdw` abbreviates `…com\demo\wwwroot…`, and `FuzzyMatch`
  also caps candidates at 128 characters as a second line of defence.
- **Names, under four characters.** Two and three letters land inside far too many ordinary
  English words. At four and up the query is specific enough that a mid-word hit is nearly
  always meant, and `count` should still find `EES.Discounts`.

Note the asymmetry this creates as you type: `ema` finds fewer cards than `emai`, because
the fourth character buys the mid-word match. That is the intended trade — the alternative
is two characters matching a tenth of the tab.

Both searches try *every* occurrence, not just the first. "main" sits mid-word in "Domain"
and at a word start in `\Main\`, and finding the mid-word one first must not settle it.

### How the abbreviation matcher decides

Requiring only that the query's characters appear in order matches nearly everything — a
three-letter query is a subsequence of most long names. Two rules narrow it to things that
read as abbreviations, both about where a *run* of matched characters may begin:

- **The first run must start at a word boundary** — the start of the name, after a
  separator, or a case step. `ersonnel` is not an abbreviation of anything.
- **A later run may start mid-word, but only if it is at least two characters long.**
  This is what reaches an all-lowercase `personnelmanagement`, which has no humps to land
  on, while still refusing `abc` the single stray `c` it would need from the middle of
  `AccountInquiryBackend`.

Scoring is all-positive, so any match scores at least 1 and 0 unambiguously means no. The
search matches every card on every keystroke, so `Score` bails out early on an exact
prefix — by far the most common case — and memoises the rest.

`FuzzyMatchTests` is deliberately half negative cases: the line between "finds what I
meant" and "lights up all 460 cards" is the whole of what this class is for.

## Layout notes

New dashboard styling lives in `wwwroot/css/dashboard.css` as plain CSS, not Tailwind.
Tailwind is a separate npm step (`npm run build-css`) that only emits classes it saw at
build time, and `output.css` is committed prebuilt — so a new utility class in markup
silently does nothing until someone reruns that build. Plain CSS in `dashboard.css`
always applies. It also backfills `.btn-sm`, `.mb-3` and `.mt-3`, which markup already
referenced but `output.css` never contained.

### Density

`.modern-card` is `p-6 rounded-xl` with a hover lift, which is right for a panel on a form
and wrong for a list of 460 of them. The dashboard overrides it per-class rather than
editing the Tailwind source — that would need an npm rebuild of a committed file, and would
change every other panel in the app along with these:

| | Was | Now |
|---|---|---|
| Page header | 4xl title + subtitle + `mb-8`, toolbar on its own row | one 33px row |
| Group card | `p-6`, `rounded-xl` | `0.5rem 0.75rem`, `rounded-lg` |
| Workspace card | `p-6` + `hover:scale-[1.02]` | `0.5rem 0.625rem`, no transform |
| Grid | `minmax(20rem)`, 1rem gap | `minmax(17rem)`, 0.5rem gap |
| Group stack | `space-y-6` | 0.75rem gap |
| Add-workspace tile | dashed box the height of a card | one row |
| Add New Group | full-width button below every card | toolbar button |

Two of those are not just about size. The `hover:scale-[1.02]` resampled the glyphs on a
card of 13px text, so everything on it went momentarily soft; and a full-width card rising
4px on hover moves every card below it, which at this density the pointer crosses several
of on the way anywhere. Both are now colour-only hovers.

The header's toolbar uses `flex: 1 1 auto` with `justify-content: flex-end`, **not**
`margin-left: auto`. An auto margin absorbs the free space instead of handing it to the
item, so the toolbar was sized as though the room were not there and wrapped its last
button onto a second row at 1280px.

`(Better)Christmas` draws a garland ring 0.5rem outside every card, so both of the
dashboard's stacks get their own gap under that theme (`themeDecor.css`) — at 0.5rem and
0.75rem the neighbouring rings would overlap. The ring's outer radius also has to be the
card's radius plus that inset, which the dense cards restate at 1rem.

### Quick open

A collapsed card shows one Open button per location, up to `quickOpenButtons` from
`ui_settings.yaml` (default 3, clamped 0–5, editable in Settings). Only while collapsed:
expanded, every location already has its own Open button a few pixels lower, and two rows
of the same buttons is worse than either. Locations past the cap collapse into a `+N` chip
that expands the card.

**Right-clicking a chip** opens the same menu the expanded row's ⋮ gives — Explorer,
Terminal, Copy Path, Open With, Run Script, Delete Location. A chip *is* that row in
miniature, and there is nowhere on a collapsed card to put a ⋮ without doubling the width
of every chip.

That menu is one `RenderFragment<WorkspaceLocation>` declared at the top of
`WorkspaceCard.razor` and rendered from both places. A templated delegate rather than a
child component because the menu needs five of the card's callbacks plus `IsReadOnly`, the
script list and the open-with options — plumbing that through parameters would be more code
than the menu is. It has to be declared above its first use: Razor emits the component body
top to bottom, so a local declared below it does not compile.

Each chip sits in its own `[data-menu-anchor]`. Without it, `menu.js`'s document listener
sees the `pointerdown` that precedes every `contextmenu` and closes the menu the right-click
is about to open — and once open, clicking an item would dismiss the menu instead of running
it.

### Collapse All

`CardStateService.SetAllExpanded` needs the card names passed in, because the dictionary
only holds cards somebody has already touched. With "Expand groups by default" on, the
untouched cards are precisely the open ones and precisely the ones missing from the
dictionary, so "collapse everything" cannot be done by walking it. Writing an explicit
entry per card on screen is also what makes the result outlast the default.

The button collapses groups *and* the workspace cards inside them — a group closed over a
dozen open cards is a group that reopens full of them. There is deliberately no Expand
counterpart: it put 457 cards and 756 location rows on screen in one frame, with a
`File.Exists` on every one of those paths on the way.

`Index` subscribes to `CardStateService.OnStateChanged` purely so the button can grey
itself out. What opens a card is a click handled inside the card, which re-renders the card
and nothing above it, so without the subscription the button stayed disabled after the
first group was expanded.

### Reordering groups

`draggable` is on the grip, not the card. The card is a click target for expanding, and a
draggable card turns every one of those clicks into a half-started drag.

Drop handlers are on the whole card — "drop onto the card that should end up below you" —
rather than on a thin strip between cards, which is a target you have to hit. `dragover`
must `preventDefault` or the browser refuses the drop. The dragged card goes to 45% opacity
rather than being hidden: a card that vanishes mid-drag takes the drop targets around it
with it, because the list reflows under the cursor.

`wwwroot/js/dashboard.js` exists because Blazor's `@ondragstart` hands C# a `DragEventArgs`
with no `dataTransfer` on it, so the two things a drag needs at dragstart time cannot be
done from a component: `setData` (Chromium starts a drag without a payload, Firefox does
not) and `setDragImage` (without it the ghost is the 10px grip glyph alone). One document
listener covers every handle marked `data-drag-handle`, with nothing to register or
dispose.

### The path overflow

Flex and grid children default to `min-width: auto`, meaning they refuse to shrink below
their longest unbreakable string. A full Windows path is exactly that, so every
container holding one was forced wider than its parent and the text ran off the card,
pushing the action buttons out with it. The fix is `min-width: 0` on every ancestor
between the card and the path — `.ws-grid > *`, `.workspace-card`, `.ws-loc`,
`.ws-loc-body` — plus a real truncation strategy on the path itself.

Paths use `PathLabel.razor`, which splits at the last separator and lets only the head
ellipsise:

```
C:\tfs\elliottelectric_com\demo\wwwr…Account.Demo.slnf
```

The file name — the part that actually distinguishes two locations — always stays
readable. The previous approach (`direction: rtl` with `width: 100%`) could not work:
`width: 100%` resolved against a parent that had already been widened by the path.

### Card identity

Cards key on **group + workspace name** (`WorkspaceViewModel.StateKey`), not on
`Workspace.Id`, and group cards are `@key`ed by name.

Ids in `workspaceGroups.yaml` had drifted into being unique only *within* a group — 453
workspaces across 282 ids, and three groups all holding id 1 — because the old loader
only filled in ids that were literally `0`. Anything keyed on an id then aliased
between unrelated cards: expanding one expanded its twins in other groups, and their
dialogs opened together.

`WorkspaceService.NormalizeIds` now repairs that on load, renumbering anything missing
or duplicated (and resyncing `Workspace.GroupName`, which had drifted to stale values
like `Solutions`) and persisting once. The name-based keys stay regardless — they are
the real identity, they survive a rescan, and they do not depend on the data staying
clean.
