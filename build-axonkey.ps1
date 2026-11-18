param(
    [string]$Source = (Join-Path $PSScriptRoot 'build\axonkey-source-v1.9.10'),
    [string]$Dotnet = 'dotnet',
    [string]$Npm = 'npm.cmd',
    [string]$Cargo = 'cargo',
    [string]$Python = 'py',
    [string]$Components = (Join-Path $PSScriptRoot 'build\components.zip'),
    [string]$Output = (Join-Path $PSScriptRoot 'build\axonkey-embedded-release'),
    [switch]$PackageOnly,
    [string]$AxonKeyDefaults = (Join-Path $env:APPDATA 'com.axonkey.app\device-instances.json')
)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
$Components = (Resolve-Path -LiteralPath $Components).Path
$Output = [IO.Path]::GetFullPath($Output)
if (!$PackageOnly) {
    if (!(Test-Path -LiteralPath (Join-Path $Source '.env'))) {
        Copy-Item -LiteralPath (Join-Path $Source '.env.example') -Destination (Join-Path $Source '.env')
    }
    & $Python (Join-Path $PSScriptRoot 'scripts\prepare-axonkey-module.py') $Source
    if ($LASTEXITCODE) { throw 'AxonKey 宿主适配失败。' }
    Push-Location -LiteralPath $Source
    try {
        & $Npm ci --no-audit --no-fund
        if ($LASTEXITCODE) { throw 'AxonKey 前端依赖安装失败。' }
        & $Npm run build
        if ($LASTEXITCODE) { throw 'AxonKey 配置界面构建失败。' }
        & $Cargo build --manifest-path src-tauri/Cargo.toml --lib --bin axonkey-cli --features tauri/custom-protocol --release --locked --target x86_64-pc-windows-msvc
        if ($LASTEXITCODE) { throw 'AxonKey 内置 DLL 构建失败。' }
    }
    finally { Pop-Location }
}

$stage = Join-Path $PSScriptRoot ('build\axonkey-stage-' + [Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $Components -DestinationPath $stage
$module = Join-Path $stage 'AxonKey'
$vendor = Join-Path $module 'vendor'
New-Item -ItemType Directory -Path $vendor -Force | Out-Null
if ($AxonKeyDefaults) {
    $defaults = Get-Item -LiteralPath $AxonKeyDefaults
    $config = Get-Content -LiteralPath $defaults.FullName -Raw | ConvertFrom-Json
    if (!$config.instances -or !$config.instances.Count) { throw 'AxonKey 默认配置没有设备。' }
    foreach ($instance in $config.instances) {
        if (!$instance.voiceTarget -and $instance.quarborTarget -match '(?i)_([0-9a-f]{12})(?:\\|$)') {
            # The original schema also uses this address to identify the input driver after re-pairing.
            $instance | Add-Member -NotePropertyName voiceTarget -NotePropertyValue $Matches[1] -Force
        }
    }
    $defaultsFolder = Join-Path $module 'Defaults'
    New-Item -ItemType Directory -Path $defaultsFolder | Out-Null
    $config | ConvertTo-Json -Depth 100 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $defaultsFolder 'device-instances.json')
}
Copy-Item -LiteralPath (Join-Path $Source 'src-tauri\target\x86_64-pc-windows-msvc\release\axonkey_lib.dll') -Destination $module
Copy-Item -LiteralPath (Join-Path $Source 'src-tauri\target\x86_64-pc-windows-msvc\release\axonkey-cli.exe') -Destination $module
Copy-Item -LiteralPath (Join-Path $Source 'THIRD_PARTY_NOTICES.md') -Destination $module
$scripts = Join-Path $module 'scripts'
New-Item -ItemType Directory -Path $scripts | Out-Null
Copy-Item -LiteralPath (Join-Path $Source 'scripts\uninstall-old-driver.exe') -Destination $scripts
foreach ($driver in @('QuarborUniversalKeyboardDriverInstaller')) {
    $driverFolder = Join-Path $vendor $driver
    New-Item -ItemType Directory -Path $driverFolder | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $Source "vendor\$driver") -File |
        Copy-Item -Destination $driverFolder
}
$archive = "$stage.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
& $Dotnet publish (Join-Path $PSScriptRoot 'DisplaySwitcher\DisplaySwitcher.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true "-p:BundledComponentsArchive=$archive" `
    -p:DebugType=None -p:DebugSymbols=false -o $Output
if ($LASTEXITCODE) { throw 'DisplaySwitcher 内置模块版打包失败。' }
