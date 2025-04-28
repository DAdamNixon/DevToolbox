param(
    [Parameter(Position=0, Mandatory=$true)]
    [string]$ProjectPath
)

function Find-PackageJsonDirs {
    param (
        [string]$dir
    )
    
    $results = @()
    
    try {
        $items = Get-ChildItem -Path $dir -Force
        
        foreach ($item in $items) {
            # Skip node_modules directories
            if ($item.Name -eq "node_modules") {
                continue
            }
            
            if ($item.PSIsContainer) {
                # Recursively search subdirectories
                $results += Find-PackageJsonDirs -dir $item.FullName
            }
            elseif ($item.Name -eq "package.json") {
                # Found a package.json, add its directory
                $results += $item.DirectoryName
            }
        }
    }
    catch {
        Write-Error "Error accessing directory $dir : $_"
    }
    
    return $results
}

Write-Host "Searching for package.json files in: $ProjectPath" -ForegroundColor Cyan

try {
    $directories = Find-PackageJsonDirs -dir $ProjectPath
    
    Write-Host "`nFound $($directories.Count) directories with package.json" -ForegroundColor Green
    
    foreach ($dir in $directories) {
        Write-Host "`nInstalling dependencies in: $dir" -ForegroundColor Yellow
        try {
            Push-Location $dir
            npm install
            Pop-Location
        }
        catch {
            Write-Error "Error installing dependencies in $dir : $_"
        }
    }
}
catch {
    Write-Error "Error: $_"
}

# Keep window open for viewing (comment this out if not needed)
Write-Host "`nPress any key to close this window..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")