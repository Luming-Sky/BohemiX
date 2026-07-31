[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,
    [switch]$RemoveSymbols
)

$ErrorActionPreference = "Stop"
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([char[]]@('\', '/'))
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
    return
}

function Remove-OutputDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    $outputPrefix = $outputRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the output root: $fullPath"
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Get-Sha256Hash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace("-", "")
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Remove-FlattenedRuntimeCopies([string]$PayloadRoot) {
    $runtimeDirectory = Join-Path $PayloadRoot "runtimes\win-x64"
    if (-not (Test-Path -LiteralPath $runtimeDirectory -PathType Container)) {
        return
    }

    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeDirectory -File -Recurse)
    foreach ($runtimeFile in $runtimeFiles) {
        $flattenedFile = Join-Path $PayloadRoot $runtimeFile.Name
        if (-not (Test-Path -LiteralPath $flattenedFile -PathType Leaf)) {
            return
        }
        if ($runtimeFile.Length -ne (Get-Item -LiteralPath $flattenedFile).Length) {
            return
        }
        if ((Get-Sha256Hash $runtimeFile.FullName) -ne (Get-Sha256Hash $flattenedFile)) {
            return
        }
    }

    Remove-OutputDirectory $runtimeDirectory
}

$payloadRoots = @($outputRoot, (Join-Path $outputRoot "forge-host"))
foreach ($payloadRoot in $payloadRoots) {
    if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
        continue
    }

    $runtimeRoot = Join-Path $payloadRoot "runtimes"
    if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
        Get-ChildItem -LiteralPath $runtimeRoot -Directory |
            Where-Object { $_.Name -notin @("win", "win-x64") } |
            ForEach-Object { Remove-OutputDirectory $_.FullName }
    }

    Remove-OutputDirectory (Join-Path $payloadRoot "libvlc\win-x86")
    Remove-OutputDirectory (Join-Path $payloadRoot "win-x64")
    Remove-OutputDirectory (Join-Path $payloadRoot "publish")
    Remove-FlattenedRuntimeCopies $payloadRoot

    Get-ChildItem -LiteralPath $payloadRoot -File -Filter "*.xml" |
        Where-Object { Test-Path -LiteralPath (Join-Path $payloadRoot ($_.BaseName + ".dll")) -PathType Leaf } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}

if ($RemoveSymbols) {
    Get-ChildItem -LiteralPath $outputRoot -File -Recurse -Filter "*.pdb" |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}
