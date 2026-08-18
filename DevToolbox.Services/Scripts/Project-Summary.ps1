param(
    [Parameter(Position=0, Mandatory=$true)]
    [string]$ProjectPath
)

# Project-Summary.ps1
#
# Answers "what am I actually looking at?" for a workspace location, without opening it.
#
# Written for a dashboard holding hundreds of workspaces, where the folder name is often the only
# thing you know about a project. It reports the branch you left it on, what it is built out of, what
# was being worked on last, and how much of the folder is build output rather than source - that last
# figure being exactly what Real-Clean would reclaim.
#
# Read-only. It creates nothing, changes nothing and deletes nothing, so it is safe to point at
# anything, including a live working copy mid-change.
#
# ASCII only and no PowerShell 7 syntax on purpose: this runs both in-process through the PowerShell
# SDK and in a terminal window through Windows PowerShell 5.1, and it has to behave the same in both.

$ProjectPath = $ProjectPath.Trim("'", '"')

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    Write-Error "Path not found: $ProjectPath"
    return
}

$root = (Resolve-Path -LiteralPath $ProjectPath).Path
if (-not (Get-Item -LiteralPath $root).PSIsContainer) {
    $root = Split-Path -Parent $root
}

# Folders whose contents are generated, not written. Excluded from every source figure below, and
# measured separately at the end.
$ArtifactNames = @('bin', 'obj', 'node_modules', '.vs', 'packages', 'TestResults')

function Write-Heading([string] $Text) {
    Write-Host ""
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ("-" * $Text.Length) -ForegroundColor DarkGray
}

function Format-Size([double] $Bytes) {
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N1} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N0} KB" -f ($Bytes / 1KB) }
    return "$Bytes bytes"
}

function Test-IsArtifact([string] $FullName) {
    foreach ($name in $ArtifactNames) {
        if ($FullName -like "*\$name\*") { return $true }
    }
    return $false
}

Write-Host ""
Write-Host "  $root" -ForegroundColor White

# Walked once. Every figure below is derived from this list, which matters on a tree with a large
# node_modules in it - and a second walk is where a filter quietly stops agreeing with the first.
$allFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -notlike '*\.git\*' })

$sourceFiles = @($allFiles | Where-Object { -not (Test-IsArtifact $_.FullName) })
$artifactFiles = @($allFiles | Where-Object { Test-IsArtifact $_.FullName })

# -- Source control ------------------------------------------------------------------------------
Write-Heading "Source control"

$gitDir = $root
$repo = $null
while ($gitDir -and -not $repo) {
    if (Test-Path -LiteralPath (Join-Path $gitDir '.git')) { $repo = $gitDir }
    else { $gitDir = Split-Path -Parent $gitDir }
}

if ($repo -and (Get-Command git -ErrorAction SilentlyContinue)) {
    Push-Location $repo
    try {
        $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
        $last = (git log -1 --format='%h  %an  %ar  %s' 2>$null)
        $dirty = @(git status --porcelain 2>$null)

        Write-Host ("  Repository   {0}" -f $repo)
        Write-Host ("  Branch       {0}" -f $branch)
        if ($last) { Write-Host ("  Last commit  {0}" -f $last) }

        if ($dirty.Count -eq 0) {
            Write-Host "  Working copy clean" -ForegroundColor Green
        }
        else {
            Write-Host ("  {0} uncommitted change(s)" -f $dirty.Count) -ForegroundColor Yellow
            $dirty | Select-Object -First 8 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
            if ($dirty.Count -gt 8) { Write-Host ("    ... and {0} more" -f ($dirty.Count - 8)) -ForegroundColor DarkYellow }
        }
    }
    finally { Pop-Location }
}
elseif (Test-Path -LiteralPath (Join-Path $root '.tfignore')) {
    # Most of this tree is TFVC, where there is no per-folder marker to find - a .tfignore is the
    # closest thing to a hint, and `tf status` needs a configured workspace, so it is not attempted.
    Write-Host "  Looks like TFVC (found .tfignore). Use Visual Studio or tf.exe for status."
}
else {
    Write-Host "  No git repository found above this folder."
}

# -- What it is built out of ---------------------------------------------------------------------
Write-Heading "Projects"

$solutions = @($sourceFiles | Where-Object { $_.Extension -eq '.sln' })

# Matched on the extension rather than passed to -Include, which Get-ChildItem silently ignores when
# the path came in as -LiteralPath: every file in the tree counted as a project.
$projects = @($sourceFiles | Where-Object { $_.Extension -match '^\.(cs|vb|fs|sql)proj$' })

Write-Host ("  {0} solution(s), {1} project(s)" -f $solutions.Count, $projects.Count)

foreach ($solution in ($solutions | Select-Object -First 5)) {
    Write-Host ("    {0}" -f $solution.FullName.Substring($root.Length).TrimStart('\'))
}
if ($solutions.Count -gt 5) { Write-Host ("    ... and {0} more" -f ($solutions.Count - 5)) }

if ($projects.Count -gt 0) {
    $frameworks = @{}
    foreach ($project in $projects) {
        $text = Get-Content -LiteralPath $project.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }

        # TargetFramework(s) for SDK-style, TargetFrameworkVersion for the older format.
        $found = [regex]::Matches($text, '<TargetFrameworks?>([^<]+)</TargetFrameworks?>')
        if ($found.Count -eq 0) { $found = [regex]::Matches($text, '<TargetFrameworkVersion>([^<]+)</TargetFrameworkVersion>') }

        foreach ($match in $found) {
            foreach ($value in $match.Groups[1].Value.Split(';')) {
                $key = $value.Trim()
                if ($key) { $frameworks[$key] = 1 + $frameworks[$key] }
            }
        }
    }

    if ($frameworks.Count -gt 0) {
        $summary = ($frameworks.GetEnumerator() | Sort-Object Value -Descending |
                    ForEach-Object { "{0} ({1})" -f $_.Key, $_.Value }) -join ', '
        Write-Host ("  Targets      {0}" -f $summary)
    }
}

# -- Recently touched ----------------------------------------------------------------------------
Write-Heading "Last worked on"

if ($sourceFiles.Count -eq 0) {
    Write-Host "  No files found."
}
else {
    $sourceFiles |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 8 |
        ForEach-Object {
            Write-Host ("  {0:yyyy-MM-dd HH:mm}  {1}" -f $_.LastWriteTime, $_.FullName.Substring($root.Length).TrimStart('\'))
        }
}

# -- Size, and what is only build output ---------------------------------------------------------
Write-Heading "Size"

$sourceBytes = ($sourceFiles | Measure-Object Length -Sum).Sum
if (-not $sourceBytes) { $sourceBytes = 0 }

$artifactBytes = ($artifactFiles | Measure-Object Length -Sum).Sum
if (-not $artifactBytes) { $artifactBytes = 0 }

Write-Host ("  Source       {0} in {1} file(s)" -f (Format-Size $sourceBytes), $sourceFiles.Count)
Write-Host ("  Build output {0}" -f (Format-Size $artifactBytes))

if ($artifactBytes -gt 0) {
    Write-Host "  Real-Clean would reclaim the build output above." -ForegroundColor DarkGray
}

Write-Host ""
