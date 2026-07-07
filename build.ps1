# NAS Connector build script
# Run from the Server-Connector directory: .\build.ps1
#   .\build.ps1            compile + deploy to your local Playnite
#   .\build.ps1 -Package   also produce NasConnector.pext for a GitHub release
param([switch]$Package)

$csc = "$PSScriptRoot\build-tools\Microsoft.Net.Compilers.Toolset.4.9.2\tasks\net472\csc.exe"
$playniteDir = "$env:LOCALAPPDATA\Playnite"
$sdk = "$PSScriptRoot\NasConnector\packages\PlayniteSDK.6.15.0\lib\net462\Playnite.SDK.dll"
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$fxDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$outDir = "$PSScriptRoot\NasConnector\bin\Release"
$projDir = "$PSScriptRoot\NasConnector"
$extDir = "$env:APPDATA\Playnite\Extensions\NasConnector_7f3a9d12-4b8e-4c21-a5f6-9e0b1c2d3e4f"

New-Item -ItemType Directory -Force $outDir | Out-Null

$sources = @(
    "$projDir\NasConnectorPlugin.cs",
    "$projDir\Models\NasGameType.cs",
    "$projDir\Models\NasGameEntry.cs",
    "$projDir\Settings\NasConnectorSettings.cs",
    "$projDir\Settings\NasConnectorSettingsView.cs",
    "$projDir\Scanner\NasLibraryScanner.cs",
    "$projDir\Scanner\NameCleaner.cs",
    "$projDir\Install\ArchiveInstaller.cs",
    "$projDir\Install\FolderCopier.cs",
    "$projDir\Install\IoRetry.cs",
    "$projDir\Install\ExecutableFinder.cs",
    "$projDir\Install\NasInstallController.cs",
    "$projDir\Install\NasUninstallController.cs",
    "$projDir\Properties\AssemblyInfo.cs"
)

$refs = @(
    $sdk,
    "$playniteDir\SharpCompress.dll",
    "$wpfDir\PresentationCore.dll",
    "$wpfDir\PresentationFramework.dll",
    "$wpfDir\WindowsBase.dll",
    "$fxDir\System.Xaml.dll",
    "$fxDir\mscorlib.dll",
    "$fxDir\System.dll",
    "$fxDir\System.Core.dll",
    "$fxDir\System.Xml.dll",
    "$fxDir\System.Security.dll"
)

$argList = @("/target:library", "/out:$outDir\NasConnector.dll", "/langversion:7.3") +
           ($refs | ForEach-Object { "/r:$_" }) +
           $sources

Write-Host "Compiling NAS Connector..."
$output = & $csc @argList 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED:" -ForegroundColor Red
    $output
    exit 1
}
Write-Host "Build succeeded." -ForegroundColor Green

# Deploy
Write-Host "Deploying to Playnite extensions..."
New-Item -ItemType Directory -Force $extDir | Out-Null
New-Item -ItemType Directory -Force "$extDir\Localization" | Out-Null
Copy-Item "$outDir\NasConnector.dll" "$extDir\" -Force
Copy-Item "$projDir\extension.yaml" "$extDir\" -Force
Copy-Item "$projDir\icon.png" "$extDir\" -Force
Copy-Item "$projDir\Localization\en_US.xaml" "$extDir\Localization\" -Force
Write-Host "Deployed. Restart Playnite to load the extension." -ForegroundColor Green

# Package a distributable .pext (a .pext is just a ZIP of the extension folder).
# Staged from fresh build output so a running Playnite can't lock the files.
if ($Package) {
    Write-Host "Packaging NasConnector.pext..."
    $stage = "$outDir\pext-stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force "$stage\Localization" | Out-Null
    Copy-Item "$outDir\NasConnector.dll" "$stage\" -Force
    Copy-Item "$projDir\extension.yaml" "$stage\" -Force
    Copy-Item "$projDir\icon.png" "$stage\" -Force
    Copy-Item "$projDir\Localization\en_US.xaml" "$stage\Localization\" -Force

    $pext = "$PSScriptRoot\NasConnector.pext"
    if (Test-Path $pext) { Remove-Item $pext -Force }
    Compress-Archive -Path "$stage\*" -DestinationPath "$pext.zip" -Force
    Move-Item "$pext.zip" $pext -Force
    Remove-Item $stage -Recurse -Force
    Write-Host "Created $pext" -ForegroundColor Green
}
