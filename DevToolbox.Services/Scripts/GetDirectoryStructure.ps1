# Parameters
param(
    [Parameter(Mandatory=$true)]
    [string]$Directory = $filePath,
    
    [Parameter(Mandatory=$false)]
    [string]$FilePattern = "*.*",
    
    [Parameter(Mandatory=$false)]
    [switch]$IncludeDirectories,
    
    [Parameter(Mandatory=$false)]
    [switch]$IncludeHidden,
    
    [Parameter(Mandatory=$false)]
    [switch]$IncludeSystem
)

# Function to check if file should be included based on attributes
function ShouldIncludeItem {
    param($item)
    
    if ($IncludeHidden -and $IncludeSystem) {
        return $true
    }
    
    $attributes = $item.Attributes
    if ($IncludeHidden -and ($attributes -band [System.IO.FileAttributes]::Hidden)) {
        return $true
    }
    if ($IncludeSystem -and ($attributes -band [System.IO.FileAttributes]::System)) {
        return $true
    }
    
    return -not (($attributes -band [System.IO.FileAttributes]::Hidden) -or 
                ($attributes -band [System.IO.FileAttributes]::System))
}

# Function to create a directory structure object
function Get-DirectoryStructure {
    param(
        [string]$Path,
        [string]$Pattern
    )
    
    $result = @{
        Name = Split-Path $Path -Leaf
        FullPath = $Path
        Files = @()
        Directories = @()
    }
    
    # Get all items in the current directory
    $items = Get-ChildItem -Path $Path -Force
    
    foreach ($item in $items) {
        if (ShouldIncludeItem $item) {
            if ($item.PSIsContainer) {
                if ($IncludeDirectories) {
                    $result.Directories += Get-DirectoryStructure -Path $item.FullName -Pattern $Pattern
                }
            }
            else {
                if ($item.Name -like $Pattern) {
                    $result.Files += @{
                        Name = $item.Name
                        FullPath = $item.FullName
                        Size = $item.Length
                        LastModified = $item.LastWriteTime
                        Extension = $item.Extension
                        Attributes = $item.Attributes.ToString()
                    }
                }
            }
        }
    }
    
    return $result
}

# Create the result object
$result = @{
    RootPath = $Directory
    FilePattern = $FilePattern
    IncludeDirectories = $IncludeDirectories
    IncludeHidden = $IncludeHidden
    IncludeSystem = $IncludeSystem
    Structure = Get-DirectoryStructure -Path $Directory -Pattern $FilePattern
}

# Output the result as JSON
$result | ConvertTo-Json -Depth 20 