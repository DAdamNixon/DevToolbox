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

Sources can also be edited in the app: **Projects → Scan Folders**. Saving from that
dialog rewrites the file, which drops any comments in it.

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

### Why the file association is the last resort

It looks like the obvious default and it is not trustworthy. Both defaults failed
silently, in different ways:

- **`.code-workspace`** had no handler registered at all — VS Code only claims it when
  the installer's "register as an editor for supported file types" box was ticked.
  `Process.Start` then throws, or pops the *How do you want to open this file?* picker.
- **`.sln`** *is* registered, to `VSLauncher.exe`, which picks a Visual Studio by
  reading the solution's `# Visual Studio Version` header. On a machine with more than
  one VS it can resolve to the wrong one — here an old VS 2019 that cannot open a
  `Version 18` solution. The launcher exits without a word.

Naming the program removes the guesswork for both.

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

## Layout notes

New dashboard styling lives in `wwwroot/css/dashboard.css` as plain CSS, not Tailwind.
Tailwind is a separate npm step (`npm run build-css`) that only emits classes it saw at
build time, and `output.css` is committed prebuilt — so a new utility class in markup
silently does nothing until someone reruns that build. Plain CSS in `dashboard.css`
always applies. It also backfills `.btn-sm`, `.mb-3` and `.mt-3`, which markup already
referenced but `output.css` never contained.

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
