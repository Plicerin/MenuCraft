param(
    [switch]$Run,
    [switch]$Publish,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$menuPlatform = if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { 'x64' } else { $env:PROCESSOR_ARCHITECTURE }
$menuBuildRoot = Join-Path $env:TEMP 'MenuCraft-local-build'
$menuProject = Join-Path $PSScriptRoot 'MenuCraft.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $menuBuildRoot 'bin' }
$menuOutput = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
$menuAction = if ($Publish) { 'publish' } else { 'build' }
$menuArguments = @($menuAction, $menuProject, '-c', $Configuration, "-p:Platform=$menuPlatform", '-p:UseSharedCompilation=false', "-p:BaseIntermediateOutputPath=$menuBuildRoot\obj\", "-p:OutputPath=$menuOutput")
if ($Publish) {
    $menuArguments += @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '-p:DebugType=None', "-p:PublishDir=$menuOutput")
}
dotnet @menuArguments
if ($LASTEXITCODE -ne 0) { throw 'MenuCraft build failed.' }
Write-Output "Built: $(Join-Path $menuOutput 'MenuCraft.exe')"
if ($Run) { Start-Process -FilePath (Join-Path $menuOutput 'MenuCraft.exe') -WorkingDirectory $menuOutput }
