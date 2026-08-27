#!/bin/bash
# Must keep LF line endings or Linux won't execute it (enforced by .gitattributes)
#
# SboxBuild resolves every path from the current working directory, so this must
# run from the repo root regardless of where it was invoked from.
set -e
cd "$(dirname "$0")"

sboxbuild() {
	dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- "$@"
}

sboxbuild build --config Developer

# build-shaders and build-content look for game/bin/managed/shadercompiler.exe and
# game/bin/win64/contentbuilder.exe, which don't exist on Linux (the native Linux
# contentbuilder lives in game/bin/linuxsteamrt64). Warn and continue rather than
# aborting the whole bootstrap - the managed build above is the part that works.
sboxbuild build-shaders || echo "warning: build-shaders failed (not supported on Linux yet), continuing"
sboxbuild build-content || echo "warning: build-content failed (not supported on Linux yet), continuing"
