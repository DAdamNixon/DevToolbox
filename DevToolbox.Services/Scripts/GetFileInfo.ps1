# Get file information
$fileInfo = Get-Item $filePath

# Create a custom object with file details
$result = [PSCustomObject]@{
    Name = $fileInfo.Name
    Size = $fileInfo.Length
    LastModified = $fileInfo.LastWriteTime
    Extension = $fileInfo.Extension
    Directory = $fileInfo.DirectoryName
}

# Output the result
$result | ConvertTo-Json 