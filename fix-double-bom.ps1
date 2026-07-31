$filePath = 'D:\BohemiX\src\BohemiX.Modules.SaveManager\Views\SaveManagerView.axaml'
$bytes = [System.IO.File]::ReadAllBytes($filePath)

# Check for double BOM and remove one
$bom = [byte[]]@(0xEF, 0xBB, 0xBF)
if ($bytes.Length -ge 6 -and 
    $bytes[0] -eq $bom[0] -and $bytes[1] -eq $bom[1] -and $bytes[2] -eq $bom[2] -and
    $bytes[3] -eq $bom[0] -and $bytes[4] -eq $bom[1] -and $bytes[5] -eq $bom[2]) {
    Write-Host "Double BOM detected, removing one..."
    $newBytes = $bytes[3..($bytes.Length-1)]
    [System.IO.File]::WriteAllBytes($filePath, $newBytes)
    Write-Host "Fixed!"
} else {
    Write-Host "No double BOM detected"
}
