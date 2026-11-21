<#
.SYNOPSIS
    Compile le driver ASCOM « Toit coulissant Arduino ».

.DESCRIPTION
    Encapsule `dotnet build` sur la solution du driver et affiche le chemin de la DLL produite.

    Le pack de ciblage .NET Framework 4.8 n'est pas installé sur cette machine : les assemblies
    de référence proviennent du paquet NuGet Microsoft.NETFramework.ReferenceAssemblies, déclaré
    dans le .csproj. Une connexion réseau est donc nécessaire à la première compilation, le temps
    de restaurer ce paquet.

.PARAMETER Configuration
    Release (défaut) ou Debug.

.PARAMETER Clean
    Force une recompilation complète (--no-incremental). À utiliser en cas de doute sur la
    fraîcheur de la DLL : une compilation incrémentale peut laisser en place un artefact périmé.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$solution   = Join-Path $repoRoot 'driver\ASCOM.ArduinoRoof.sln'
$outputDll  = Join-Path $repoRoot "driver\ASCOM.ArduinoRoof.Dome\bin\$Configuration\net48\ASCOM.ArduinoRoof.Dome.dll"

Write-Host ''
Write-Host '=== Compilation du driver ASCOM ===' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $solution)) {
    Write-Host "Solution introuvable : $solution" -ForegroundColor Red
    exit 1
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host 'Le SDK .NET est introuvable dans le PATH.' -ForegroundColor Red
    Write-Host 'Installez-le avec :  winget install Microsoft.DotNet.SDK.8' -ForegroundColor Yellow
    exit 1
}

Write-Host "Solution      : $solution"
Write-Host "Configuration : $Configuration"
Write-Host ''

$buildArgs = @('build', $solution, '-c', $Configuration, '--nologo')
if ($Clean) {
    $buildArgs += '--no-incremental'
    Write-Host 'Recompilation complète demandée (--no-incremental).' -ForegroundColor DarkGray
    Write-Host ''
}

& dotnet @buildArgs
$buildExit = $LASTEXITCODE

Write-Host ''
if ($buildExit -ne 0) {
    Write-Host 'ÉCHEC de la compilation.' -ForegroundColor Red
    exit $buildExit
}

if (-not (Test-Path $outputDll)) {
    Write-Host "La compilation a réussi mais la DLL est introuvable : $outputDll" -ForegroundColor Red
    exit 1
}

$dll = Get-Item $outputDll
Write-Host 'Compilation réussie.' -ForegroundColor Green
Write-Host ''
Write-Host "  DLL       : $($dll.FullName)"
Write-Host ("  Taille    : {0:N0} octets" -f $dll.Length)
Write-Host "  Modifiée  : $($dll.LastWriteTime)"
Write-Host ''

# Garde-fou : une DLL anormalement petite trahit presque toujours une compilation
# incrémentale périmée, symptôme observé pendant le développement.
if ($dll.Length -lt 20000) {
    Write-Host 'ATTENTION : la DLL semble anormalement petite.' -ForegroundColor Yellow
    Write-Host 'Relancez avec -Clean pour forcer une recompilation complète.' -ForegroundColor Yellow
    Write-Host ''
}

Write-Host 'Étape suivante — enregistrement COM, dans un terminal ADMINISTRATEUR :' -ForegroundColor Cyan
Write-Host "    powershell -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'register.ps1')`"" -ForegroundColor DarkGray
Write-Host ''
exit 0
