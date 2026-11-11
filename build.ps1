param(
    [string]$Dotnet = (Join-Path $env:LOCALAPPDATA 'DisplaySwitcherSdk\dotnet.exe'),
    [string]$Python = (Join-Path $env:LOCALAPPDATA 'DisplaySwitcherFanBuild\Scripts\python.exe'),
    [string]$FanDependencies = (Join-Path $PSScriptRoot 'FanWorker\lib'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Release')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$workspaceBuild = Join-Path $PSScriptRoot 'build'
$monitorArchive = Join-Path $workspaceBuild 'multimonitortool-x64.zip'
$previousDependencies = $env:DISPLAY_SWITCHER_FAN_LIB_DIR
$env:DISPLAY_SWITCHER_FAN_LIB_DIR = (Resolve-Path -LiteralPath $FanDependencies).ProviderPath
Push-Location -LiteralPath $PSScriptRoot
try {
    New-Item -ItemType Directory -Force -Path $workspaceBuild | Out-Null
    if (-not (Test-Path -LiteralPath $monitorArchive)) {
        $download = "$monitorArchive.download"
        Invoke-WebRequest -Uri 'https://www.nirsoft.net/utils/multimonitortool-x64.zip' -OutFile $download
        Move-Item -LiteralPath $download -Destination $monitorArchive
    }

    $fanArgs = @(
        '-m', 'PyInstaller', '--noconfirm', '--distpath', (Join-Path $workspaceBuild 'fan'),
        '--workpath', (Join-Path $workspaceBuild 'pyinstaller'), '.\FanWorker\worker.spec'
    )
    & $Python @fanArgs
    if ($LASTEXITCODE -ne 0) { throw '温控助手构建失败。' }

    $componentStage = Join-Path $workspaceBuild ('components-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $componentStage | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspaceBuild 'fan\FanWorker.exe') -Destination $componentStage
    Expand-Archive -LiteralPath $monitorArchive -DestinationPath (Join-Path $componentStage 'MultiMonitorTool')
    $componentArchive = Join-Path $workspaceBuild 'components.zip'
    Compress-Archive -Path (Join-Path $componentStage '*') -DestinationPath $componentArchive -Force

    $publishArgs = @(
        'publish', '.\DisplaySwitcher\DisplaySwitcher.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        "-p:BundledComponentsArchive=$componentArchive",
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $output
    )
    & $Dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw '显示器切换主程序构建失败。' }

    Write-Output (Join-Path $output 'DisplaySwitcher.exe')
}
finally {
    $env:DISPLAY_SWITCHER_FAN_LIB_DIR = $previousDependencies
    Pop-Location
}
