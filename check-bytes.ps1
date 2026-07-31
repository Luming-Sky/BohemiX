$bytes = [System.IO.File]::ReadAllBytes('D:\BohemiX\src\BohemiX.Modules.SaveManager\Views\SaveManagerView.axaml')
$first10 = $bytes | Select-Object -First 10
$hex = $first10 | ForEach-Object { '0x{0:X2}' -f $_ }
Write-Host "First 10 bytes: $hex"
