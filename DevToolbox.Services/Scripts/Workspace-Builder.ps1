param(
    [Parameter(Mandatory=$true)]
    [string]$RootDirectory,
    [string]$OutputFile = "workspaces.yaml"
)

# Find all solution files and group by name
$solutionGroups = Get-ChildItem -Path $RootDirectory -Filter "*.sln" -Recurse | 
    Group-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }

# Initialize YAML structure
$yaml = @"
workspaceGroups:
  - name: Solutions
    workspaces:

"@

foreach ($group in $solutionGroups) {
    $workspaceName = $group.Name
    
    # Add workspace entry
    $yaml += @"
      - name: $workspaceName
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
}

# Write to file
$yaml | Out-File -FilePath $OutputFile -Encoding UTF8

Write-Host "YAML configuration has been written to $OutputFile"