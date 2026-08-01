[CmdletBinding()]
param(
    [string]$Version = "0.9.1",
    [string]$NativeBundlePath,
    [ValidateNotNullOrEmpty()]
    [string]$UsvfsVersion = "0.5.7.2",
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$UsvfsSha256 = "7EE7758433AB76713900E661056BE8074B9C567971FDE38FD0E514C76895E274",
    [string]$OutputDirectory = "publish",
    [Parameter(DontShow = $true)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
} else {
    (Resolve-Path $RepositoryRoot).Path
}
if ([string]::IsNullOrWhiteSpace($NativeBundlePath)) {
    $NativeBundlePath = Join-Path $repoRoot "third-party\usvfs\v0.5.7.2\win-x64"
}
$nativeBundle = (Resolve-Path $NativeBundlePath).Path
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$workRoot = Join-Path $repoRoot ".build-temp\release-$Version"
$publishDirectory = Join-Path $workRoot "publish"
$packageDirectory = Join-Path $workRoot "package\BohemiX-$Version-win-x64"
$zipPath = Join-Path $outputRoot "BohemiX-$Version-win-x64.zip"
$licenseFile = Get-ChildItem -LiteralPath $repoRoot -File |
    Where-Object { $_.Name -in @("LICENSE", "LICENSE.txt", "LICENSE.md") } |
    Select-Object -First 1

if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Version must be a semantic version such as 0.7.0 or 0.7.0-rc.1."
}
$assemblyVersion = "$(($Version -split '[-+]')[0]).0"

if (-not (Test-Path -LiteralPath $nativeBundle -PathType Container)) {
    throw "Native bundle directory does not exist: $nativeBundle"
}
if (-not $licenseFile) {
    throw "A reviewed root LICENSE file is required before publishing."
}
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -PathType Leaf)) {
    throw "THIRD-PARTY-NOTICES.md is required before publishing."
}
$nativeLicense = Get-ChildItem -LiteralPath $nativeBundle -File |
    Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\..+)?$' } |
    Select-Object -First 1
if (-not $nativeLicense) {
    throw "The approved native bundle must include its upstream LICENSE, COPYING, or NOTICE file."
}
$nativeVersionFile = Join-Path $nativeBundle "USVFS-VERSION.txt"
if (-not (Test-Path -LiteralPath $nativeVersionFile -PathType Leaf)) {
    throw "The approved native bundle must include USVFS-VERSION.txt."
}
$declaredUsvfsVersion = (Get-Content -LiteralPath $nativeVersionFile -Raw).Trim()
if ($declaredUsvfsVersion -ne $UsvfsVersion.Trim()) {
    throw "usvfs version mismatch. Expected $UsvfsVersion, got $declaredUsvfsVersion."
}

$nativeCandidates = @(@("usvfs.dll", "usvfs_x64.dll") |
    ForEach-Object { Join-Path $nativeBundle $_ } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
if ($nativeCandidates.Count -ne 1) {
    throw "Native bundle must contain exactly one of usvfs.dll or usvfs_x64.dll."
}
$nativeCandidate = $nativeCandidates[0]

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
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

function Assert-X64Dll([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64) {
            throw "usvfs is not a valid PE file: $Path"
        }
        $stream.Position = 0
        if ($reader.ReadUInt16() -ne 0x5a4d) {
            throw "usvfs does not contain a DOS MZ signature: $Path"
        }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ($peOffset + 24) -gt $stream.Length) {
            throw "usvfs has an invalid PE header: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "usvfs does not contain a PE signature: $Path"
        }
        $machine = $reader.ReadUInt16()
        $numberOfSections = $reader.ReadUInt16()
        $stream.Position = $peOffset + 20
        $sizeOfOptionalHeader = $reader.ReadUInt16()
        $stream.Position = $peOffset + 22
        $characteristics = $reader.ReadUInt16()
        if ($sizeOfOptionalHeader -lt 2 -or ($peOffset + 24 + $sizeOfOptionalHeader) -gt $stream.Length) {
            throw "usvfs has an invalid PE optional header: $Path"
        }
        $sectionTableEnd = $peOffset + 24 + $sizeOfOptionalHeader + ($numberOfSections * 40)
        if ($sectionTableEnd -gt $stream.Length) {
            throw "usvfs has an invalid PE section table: $Path"
        }
        $stream.Position = $peOffset + 24
        $optionalHeaderMagic = $reader.ReadUInt16()
        if ($machine -ne 0x8664 `
            -or $numberOfSections -eq 0 `
            -or $optionalHeaderMagic -ne 0x020b `
            -or ($characteristics -band 0x0002) -eq 0 `
            -or ($characteristics -band 0x2000) -eq 0) {
            throw "usvfs must be an x64 Windows DLL: $Path"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

Assert-X64Dll $nativeCandidate
$actualUsvfsSha256 = Get-Sha256 $nativeCandidate
if ($actualUsvfsSha256 -ne $UsvfsSha256.ToUpperInvariant()) {
    throw "usvfs SHA-256 mismatch. Expected $UsvfsSha256, got $actualUsvfsSha256."
}

Write-Host "Running Release tests..."
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
dotnet test (Join-Path $repoRoot "BohemiX.sln") -c Release --artifacts-path (Join-Path $workRoot "test-artifacts") --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Write-Host "Publishing self-contained win-x64 payload..."
dotnet publish (Join-Path $repoRoot "src\BohemiX.App\BohemiX.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$Version -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$assemblyVersion `
    -p:InformationalVersion=$Version -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Release publish failed with exit code $LASTEXITCODE."
}

& (Join-Path $repoRoot "tools\trim-win-x64-output.ps1") `
    -OutputDirectory $publishDirectory `
    -RemoveSymbols

$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishDirectory "BohemiX.App.exe")).VersionInfo.ProductVersion
if ($publishedVersion -ne $Version) {
    throw "Published product version mismatch. Expected $Version, got $publishedVersion."
}

$nativeOutput = Join-Path $publishDirectory "native"
New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
Get-ChildItem -LiteralPath $nativeBundle -Force |
    Copy-Item -Destination $nativeOutput -Recurse -Force
$manifestLines = Get-ChildItem -LiteralPath $nativeOutput -File -Recurse |
    Where-Object { $_.Name -ne "SHA256SUMS" } |
    Sort-Object FullName |
    ForEach-Object {
        $nativePrefix = [IO.Path]::GetFullPath($nativeOutput).TrimEnd('\') + '\'
        $relative = $_.FullName.Substring($nativePrefix.Length).Replace('\', '/')
        "$(Get-Sha256 $_.FullName)  $relative"
    }
if (-not $manifestLines) {
    throw "The native bundle contains no files."
}
$manifestLines | Set-Content -LiteralPath (Join-Path $nativeOutput "SHA256SUMS") -Encoding ascii

$required = @(
    "BohemiX.App.exe",
    "Microsoft.Web.WebView2.Core.dll",
    "libvlc\win-x64\libvlc.dll",
    "e_sqlite3.dll",
    "native\SHA256SUMS",
    "native\USVFS-VERSION.txt"
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relative) -PathType Leaf)) {
        throw "Published payload is missing required dependency: $relative"
    }
}
if (-not (Get-ChildItem -LiteralPath $nativeOutput -File -Recurse | Where-Object Name -in @("usvfs.dll", "usvfs_x64.dll"))) {
    throw "Published payload is missing the verified usvfs binary."
}
$publishedUsvfs = Get-ChildItem -LiteralPath $nativeOutput -File -Recurse |
    Where-Object Name -in @("usvfs.dll", "usvfs_x64.dll") |
    Select-Object -First 1
Assert-X64Dll $publishedUsvfs.FullName
if ((Get-Sha256 $publishedUsvfs.FullName) -ne $actualUsvfsSha256) {
    throw "Published usvfs SHA-256 does not match the approved native bundle."
}
if (Test-Path -LiteralPath (Join-Path $publishDirectory "libvlc\win-x86")) {
    throw "Published payload unexpectedly contains the unsupported LibVLC x86 runtime."
}
$unsupportedRuntimeDirectories = @($publishDirectory, (Join-Path $publishDirectory "forge-host")) |
    ForEach-Object { Join-Path $_ "runtimes" } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
    ForEach-Object { Get-ChildItem -LiteralPath $_ -Directory } |
    Where-Object { $_.Name -notin @("win", "win-x64") }
if ($unsupportedRuntimeDirectories) {
    $unsupportedNames = ($unsupportedRuntimeDirectories.FullName -join ", ")
    throw "Published payload unexpectedly contains unsupported runtime directories: $unsupportedNames"
}

Copy-Item -LiteralPath $licenseFile.FullName -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") -Destination $publishDirectory

Get-ChildItem -LiteralPath $publishDirectory -Force |
    Copy-Item -Destination $packageDirectory -Recurse -Force
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = Get-Sha256 $zipPath
"$zipHash  $(Split-Path $zipPath -Leaf)" | Set-Content -LiteralPath (Join-Path $outputRoot "SHA256SUMS.txt") -Encoding ascii
Write-Host "Created $zipPath"
Write-Host "SHA-256: $zipHash"
