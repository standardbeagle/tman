$script:AssetMap = @{
    'linux-x64'   = 'tman-linux-x64.tar.gz'
    'linux-arm64' = 'tman-linux-arm64.tar.gz'
    'osx-x64'     = 'tman-osx-x64.tar.gz'
    'osx-arm64'   = 'tman-osx-arm64.tar.gz'
    'win-x64'     = 'tman-win-x64.zip'
}

function Get-TmanRid {
    $os = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win' }
          elseif ($IsMacOS) { 'osx' }
          else { 'linux' }
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $a = switch ($arch) {
        'X64'   { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "tman: unsupported architecture $arch" }
    }
    return "$os-$a"
}

function Get-TmanDir {
    $base = if ($IsWindows -or $env:OS -eq 'Windows_NT') { $env:LOCALAPPDATA } else { Join-Path $HOME '.local' }
    return Join-Path $base 'tman'
}

function Get-TmanExe {
    $name = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'tman.exe' } else { 'tman' }
    return Join-Path (Get-TmanDir) $name
}

function Install-Tman {
    [CmdletBinding()]
    param([string]$Version = '0.1.0')

    $rid = Get-TmanRid
    $asset = $script:AssetMap[$rid]
    if (-not $asset) { throw "tman: no asset for $rid" }

    $url = "https://github.com/standardbeagle/tman/releases/download/v$Version/$asset"
    $dir = Get-TmanDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $archive = Join-Path $dir $asset

    Write-Host "tman: downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $archive

    if ($asset.EndsWith('.zip')) {
        Expand-Archive -Path $archive -DestinationPath $dir -Force
    } else {
        tar -xzf $archive -C $dir
    }
    Remove-Item $archive

    $exe = Get-TmanExe
    if (-not (Test-Path $exe)) { throw "tman: archive did not contain the binary" }
    if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) { chmod +x $exe }
    Write-Host "tman: installed to $exe"
}

function Invoke-Tman {
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)

    $exe = Get-TmanExe
    if (-not (Test-Path $exe)) { Install-Tman }
    & $exe @Args
    exit $LASTEXITCODE
}

Set-Alias -Name tman -Value Invoke-Tman
Export-ModuleMember -Function Install-Tman, Invoke-Tman -Alias tman
