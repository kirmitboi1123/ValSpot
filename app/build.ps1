# Builds publish\ValSpot.exe (one self-contained file) and ValSpot-share.zip.
# Needs the .NET 10 SDK (dotnet on PATH, or in %USERPROFILE%\.dotnet).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# prefer the per-user SDK: a system-wide dotnet may only have runtimes (no SDK)
$userDotnet = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $userDotnet 'sdk')) { $env:PATH = "$userDotnet;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish -c Release -o publish -nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Copy-Item README.txt publish\README.txt -Force
Compress-Archive -Path publish\ValSpot.exe, publish\README.txt -DestinationPath ValSpot-share.zip -Force
Write-Output ('Built publish\ValSpot.exe ({0:N0} MB) and ValSpot-share.zip ({1:N0} MB)' -f ((Get-Item publish\ValSpot.exe).Length / 1MB), ((Get-Item ValSpot-share.zip).Length / 1MB))
