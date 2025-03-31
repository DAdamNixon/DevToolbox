# Real-Clean.ps1
# Script to clean all build artifacts from a .NET project
# Removes bin, obj, node_modules, .vs folders and package-lock.json files

param(
    [Parameter(Mandatory=$true)]
    [string]$ProjectPath
)

# Ensure the path exists
if (-not (Test-Path -Path $ProjectPath)) {
    Write-Error "The specified path '$ProjectPath' does not exist."
    exit 1
}

# Convert to absolute path if relative
$ProjectPath = Resolve-Path $ProjectPath

Write-Host "Cleaning build artifacts from: $ProjectPath" -ForegroundColor Cyan

# Define patterns to remove
$foldersToRemove = @("bin", "obj", "node_modules", ".vs")
$filesToRemove = @("package-lock.json")
$totalFoldersRemoved = 0
$totalFilesRemoved = 0

# Function to recursively remove folders
function Remove-Folders {
    param (
        [string]$BasePath,
        [string[]]$FolderPatterns
    )
    
    $script:totalFoldersRemoved = 0
    
    foreach ($folderPattern in $FolderPatterns) {
        Write-Host "Searching for '$folderPattern' folders..." -ForegroundColor Yellow
        $folders = Get-ChildItem -Path $BasePath -Directory -Filter $folderPattern -Recurse
        
        foreach ($folder in $folders) {
            Write-Host "Removing: $($folder.FullName)" -ForegroundColor Red
            try {
                Remove-Item -Path $folder.FullName -Recurse -Force -ErrorAction Stop
                $script:totalFoldersRemoved++
            }
            catch {
                Write-Warning "Failed to remove $($folder.FullName): $_"
            }
        }
    }
}

# Function to recursively remove files
function Remove-Files {
    param (
        [string]$BasePath,
        [string[]]$FilePatterns
    )
    
    $script:totalFilesRemoved = 0
    
    foreach ($filePattern in $FilePatterns) {
        Write-Host "Searching for '$filePattern' files..." -ForegroundColor Yellow
        $files = Get-ChildItem -Path $BasePath -File -Filter $filePattern -Recurse
        
        foreach ($file in $files) {
            Write-Host "Removing: $($file.FullName)" -ForegroundColor Red
            try {
                Remove-Item -Path $file.FullName -Force -ErrorAction Stop
                $script:totalFilesRemoved++
            }
            catch {
                Write-Warning "Failed to remove $($file.FullName): $_"
            }
        }
    }
}

# Remove folders
Remove-Folders -BasePath $ProjectPath -FolderPatterns $foldersToRemove

# Remove files
Remove-Files -BasePath $ProjectPath -FilePatterns $filesToRemove

# Report results
Write-Host "`nCleanup complete!" -ForegroundColor Green
Write-Host "Removed $totalFoldersRemoved folders and $totalFilesRemoved files." -ForegroundColor Green

# Check if dotnet-built artifacts were removed
if ((Get-ChildItem -Path $ProjectPath -Directory -Filter "bin" -Recurse).Count -gt 0 -or 
    (Get-ChildItem -Path $ProjectPath -Directory -Filter "obj" -Recurse).Count -gt 0) {
    Write-Warning "Some 'bin' or 'obj' folders could not be removed. Try closing Visual Studio or any other applications that might be locking these folders."
}

# Run dotnet clean as an extra measure
try {
    Write-Host "`nRunning 'dotnet clean' on the solution..." -ForegroundColor Cyan
    $slnFiles = Get-ChildItem -Path $ProjectPath -Filter "*.sln" -Recurse | Select-Object -First 1
    if ($slnFiles) {
        $output = dotnet clean $slnFiles.FullName
        Write-Host $output
    } else {
        Write-Host "No solution file found. Skipping 'dotnet clean'." -ForegroundColor Yellow
    }
}
catch {
    Write-Warning "Failed to run 'dotnet clean': $_"
} 