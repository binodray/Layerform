$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path (Split-Path -Parent $PSScriptRoot) '.dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$workspaceSdk = 'C:\tmp\compositor-dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceSdk) {
    $workspaceSdk
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnet @args
exit $LASTEXITCODE
