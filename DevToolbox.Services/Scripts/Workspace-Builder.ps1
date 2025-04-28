param(
    [Parameter(Position=0, Mandatory=$true)]
    [string]$ProjectPath,
    [Parameter(Position=1)]
    [string]$OutputFile = "workspaceGroups.yaml"
)

# Find all solution files and group by name
$solutionGroups = Get-ChildItem -Path $ProjectPath -Filter "*.sln" -Recurse | 
    Group-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }

# Initialize YAML structure
$yaml = @"
- id: 1
  name: Solutions
  workspaces:

"@

$workspaceId = 1
foreach ($group in $solutionGroups) {
    $workspaceName = $group.Name
    
    # Add workspace entry
    $yaml += @"
      - id: $workspaceId
        name: $workspaceName
        groupName: Solutions
        locations:
"@
    
    foreach ($sln in $group.Group) {
        $solutionDir = $sln.DirectoryName
        
        # Determine environment from path
        $environment = "main"
        if ($solutionDir -match "dev") {
            $environment = "dev"
        } elseif ($solutionDir -match "demo") {
            $environment = "demo"
        }
        
        $yaml += @"

          - name: $environment
            path: $($sln.FullName)
            root: $solutionDir
            type: Solution
"@
    }
    $yaml += "`n"
    $workspaceId++
}

# Write to file
$yaml | Out-File -FilePath $OutputFile -Encoding UTF8

Write-Host "YAML configuration has been written to $OutputFile"

# Keep window open for viewing
Write-Host "`nPress any key to close this window..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")