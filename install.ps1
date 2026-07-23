# tman installer: irm https://raw.githubusercontent.com/standardbeagle/tman/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'

$Repo = 'standardbeagle/tman'
$Dest = if ($env:TMAN_INSTALL_DIR) { $env:TMAN_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\tman' }

$arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64'   { 'x64' }
    'Arm64' { 'arm64' }
    default { throw "tman: unsupported architecture $_" }
}
$Asset = "tman-win-$arch.zip"

$Version = $env:TMAN_VERSION
if (-not $Version) {
    $Version = (Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest").tag_name
}
if (-not $Version.StartsWith('v')) { $Version = "v$Version" }

$Url = "https://github.com/$Repo/releases/download/$Version/$Asset"
Write-Host "tman: downloading $Url"

$Tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "tman-install-$([guid]::NewGuid().ToString('N'))")
try {
    $ZipPath = Join-Path $Tmp $Asset
    Invoke-WebRequest -Uri $Url -OutFile $ZipPath
    Expand-Archive -Path $ZipPath -DestinationPath $Tmp -Force

    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    Copy-Item (Join-Path $Tmp 'tman.exe') (Join-Path $Dest 'tman.exe') -Force
    Write-Host "tman: installed $Version to $Dest\tman.exe"
} finally {
    Remove-Item -Recurse -Force $Tmp
}

$UserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($UserPath -notlike "*$Dest*") {
    [Environment]::SetEnvironmentVariable('Path', "$UserPath;$Dest", 'User')
    Write-Host "tman: added $Dest to your user PATH (restart your shell)"
}
