$filePath = 'D:\BohemiX\src\BohemiX.Modules.SaveManager\Views\SaveManagerView.axaml'
$content = Get-Content $filePath -Raw -Encoding UTF8
$content = $content.TrimStart()
[System.IO.File]::WriteAllText($filePath, $content, [System.Text.Encoding]::UTF8)
Write-Host "File fixed successfully"
