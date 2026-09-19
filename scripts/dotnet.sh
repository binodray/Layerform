#!/bin/sh
# Runs the .NET 8 SDK that is installed for this project (see README).
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.compositor-dotnet-home}"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DOTNET="${COMPOSITOR_DOTNET:-/c/tmp/compositor-dotnet/dotnet.exe}"
[ -x "$DOTNET" ] || DOTNET=dotnet
exec "$DOTNET" "$@"
