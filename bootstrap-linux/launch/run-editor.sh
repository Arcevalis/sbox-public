#!/bin/bash
# Launch the s&box editor with the engine's own HarfBuzz preloaded.
#
# The engine ships libHarfBuzzSharp.so (SkiaSharp's statically-linked HarfBuzz) in
# game/bin/linuxsteamrt64, but the system's libharfbuzz.so.0 also ends up in the
# process - pulled in through Qt's xcb platform plugin, fontconfig and the GTK
# portal used for file dialogs. Both export the same unversioned hb_* names, so
# calls get spread across two copies: an hb_buffer allocated by one is handed to
# the other's free(), and glibc aborts with "free(): invalid pointer".
#
# LD_PRELOAD puts the engine's copy first in the global symbol scope, so every
# hb_* reference in the process - the engine's and the system libraries' - binds
# to that single implementation.
#
# Env overrides:
#   SBOX_EXE=sbox            launch a different binary from game/ (default sbox-dev)
#   SBOX_HARFBUZZ=/path.so   preload a different HarfBuzz (e.g. the system one, to
#                            force everything onto that copy instead)
#   SBOX_QT_PLATFORM=xcb     override Qt platform (default xcb; wayland not shipped)
#
# Usage:
#   run-editor.sh -project fss.bloodsigil
#   run-editor.sh -project "/mnt/blue/S&box Projects/bloodsigil/bloodsigil.sbproj"
#   run-editor.sh -project "*.sbproj"
#   run-editor.sh -project "bloodsigil.sbproj"
set -euo pipefail

# This lives in bootstrap-linux/launch/, so the repo root is two levels up.
LAUNCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$LAUNCH_DIR/../.." && pwd)"
GAME_DIR="$ROOT/game"
NATIVE_DIR="$GAME_DIR/bin/linuxsteamrt64"

EXE_NAME="${SBOX_EXE:-sbox-dev}"
EXE="$GAME_DIR/$EXE_NAME"
HARFBUZZ="${SBOX_HARFBUZZ:-$NATIVE_DIR/libHarfBuzzSharp.so}"

if [ ! -f "$HARFBUZZ" ]; then
	echo "error: HarfBuzz not found at $HARFBUZZ" >&2
	echo "       run ./bootstrap.sh first, or set SBOX_HARFBUZZ to the library to preload" >&2
	exit 1
fi

if [ ! -x "$EXE" ]; then
	echo "error: $EXE not found or not executable" >&2
	echo "       run ./bootstrap.sh first, or set SBOX_EXE to a binary in $GAME_DIR" >&2
	exit 1
fi

# The launchers are framework-dependent and pinned to game/dotnet via
# AppHostRelativeDotNet=dotnet (engine/Tools/SboxBuild/Steps/BuildManaged.cs).
# That pin means DOTNET_ROOT / /usr/share/dotnet are ignored -- if game/dotnet
# only has an old 8.0 runtime the net10.0 apphost fails even though the system
# has 10.0 installed.
DOTNET_DIR="$GAME_DIR/dotnet"
have_net10=0
for d in "$DOTNET_DIR/shared/Microsoft.NETCore.App/"10.* ; do
	[ -d "$d" ] && have_net10=1 && break
done
if [ "$have_net10" -eq 0 ]; then
	# Try to self-heal by staging the system 10.x runtime into game/dotnet, the
	# same thing BuildManaged:StageLinuxRuntime does during `sboxbuild build`.
	dotnet_root="${DOTNET_ROOT:-}"
	if [ -z "$dotnet_root" ]; then
		dotnet_bin="$(command -v dotnet 2>/dev/null || true)"
		if [ -n "$dotnet_bin" ]; then
			if command -v realpath >/dev/null 2>&1; then
				dotnet_bin="$(realpath "$dotnet_bin" 2>/dev/null || echo "$dotnet_bin")"
			else
				dotnet_bin="$(readlink -f "$dotnet_bin" 2>/dev/null || echo "$dotnet_bin")"
			fi
			dotnet_root="$(dirname "$dotnet_bin")"
		fi
		# dotnet-install.sh layout and some distros use DOTNET_ROOT explicitly;
		# also check the install_location file that Fedora/RHEL populate.
		if [ ! -d "$dotnet_root/shared" ] && [ -f /etc/dotnet/install_location ]; then
			dotnet_root="$(cat /etc/dotnet/install_location 2>/dev/null || echo "$dotnet_root")"
		fi
	fi
	if [ -n "${dotnet_root:-}" ] && [ -d "$dotnet_root/shared/Microsoft.NETCore.App" ]; then
		best=""
		best_ver=""
		for d in "$dotnet_root/shared/Microsoft.NETCore.App/"10.* ; do
			[ -d "$d" ] || continue
			ver="$(basename "$d")"
			if [ -z "$best" ] || printf '%s\n%s\n' "$best_ver" "$ver" | sort -V | tail -n1 | grep -qx "$ver"; then
				best="$d"
				best_ver="$ver"
			fi
		done
		if [ -n "$best" ]; then
			echo "staging .NET $best_ver runtime into $DOTNET_DIR (was missing net10)" >&2
			mkdir -p "$DOTNET_DIR/shared/Microsoft.NETCore.App" "$DOTNET_DIR/host"
			# Don't wipe the whole staging dir - just ensure the required 10.x FX is present
			# (BuildManaged will do a full clean on next `sboxbuild build`).
			rm -rf "$DOTNET_DIR/shared/Microsoft.NETCore.App/$best_ver"
			cp -a "$best" "$DOTNET_DIR/shared/Microsoft.NETCore.App/"
			if [ -d "$dotnet_root/host" ]; then
				cp -a "$dotnet_root/host/." "$DOTNET_DIR/host/"
			fi
		else
			echo "error: $DOTNET_DIR has no net10 runtime and none found under $dotnet_root" >&2
			echo "       install .NET 10 SDK/runtime or run: sboxbuild build" >&2
			exit 1
		fi
	else
		echo "error: $DOTNET_DIR has no net10 runtime (found only net8 or nothing)" >&2
		echo "       the apphost targets net10.0 and is pinned to game/dotnet via AppHostRelativeDotNet" >&2
		echo "       fix: run 'sboxbuild build' or install .NET 10 and re-run this script" >&2
		exit 1
	fi
fi

# The repo only ships the xcb platform plugin (game/bin/linuxsteamrt64/qt5_plugins/platforms/libqxcb.so
# - Qt 5.15.2). Wayland sessions set QT_QPA_PLATFORM=wayland, which then fails with
# "Could not find the Qt platform plugin 'wayland' in ''". Force xcb. Callers that
# really want wayland can set SBOX_QT_PLATFORM=wayland to override.
if [ -n "${SBOX_QT_PLATFORM:-}" ]; then
	export QT_QPA_PLATFORM="$SBOX_QT_PLATFORM"
elif [ "${QT_QPA_PLATFORM:-}" = "wayland" ] || [ -z "${QT_QPA_PLATFORM:-}" ]; then
	export QT_QPA_PLATFORM=xcb
fi

# Prepend rather than overwrite - keep anything the caller (or Steam) already set.
export LD_PRELOAD="$HARFBUZZ${LD_PRELOAD:+:$LD_PRELOAD}"
export LD_LIBRARY_PATH="$NATIVE_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# --- -project handling: supports <org>.<ident> or path/glob to .sbproj ---
# Normalizes the value after -project to an absolute .sbproj path so the engine
# (Project.NormalizeConfigFilePath) can load it regardless of cwd or '&'/spaces.
resolve_project_arg() {
	local raw="$1"
	local found=""

	# 1) Glob pattern (contains * or ?) — search via find with -name
	if [[ "$raw" == *"*"* ]] || [[ "$raw" == *\?* ]]; then
		local pat="$raw"
		# If pattern has no slash, search common roots
		if [[ "$pat" != */* ]]; then
			for root in "$PWD" "/mnt/blue/S&box Projects" "$HOME/S&box Projects" "$ROOT" "$GAME_DIR" "$ROOT/game/addons" "$ROOT/game/samples"; do
				[ -d "$root" ] || continue
				found="$(find "$root" -maxdepth 4 -name "$pat" -print -quit 2>/dev/null || true)"
				[ -n "$found" ] && break
			done
		else
			# Pattern includes path — try bash glob expansion first
			# shellcheck disable=SC2086
			for f in $pat; do [ -e "$f" ] && { found="$f"; break; }; done
			if [ -z "$found" ]; then
				local dir="${pat%/*}"; local base="${pat##*/}"
				[ -d "$dir" ] && found="$(find "$dir" -maxdepth 1 -name "$base" -print -quit 2>/dev/null || true)"
			fi
		fi
		if [ -n "$found" ]; then
			if command -v realpath >/dev/null 2>&1; then
				found="$(realpath "$found" 2>/dev/null || echo "$found")"
			else
				found="$(readlink -f "$found" 2>/dev/null || echo "$found")"
			fi
			echo "$found"
			return 0
		fi
		echo "error: no .sbproj matches pattern '$raw'" >&2
		return 1
	fi

	# 2) Direct .sbproj file (absolute or relative)
	if [[ "$raw" == *.sbproj ]]; then
		if [ -f "$raw" ]; then
			if command -v realpath >/dev/null 2>&1; then
				echo "$(realpath "$raw" 2>/dev/null || echo "$raw")"
			else
				echo "$(readlink -f "$raw" 2>/dev/null || echo "$raw")"
			fi
			return 0
		fi
		# Try relative to common roots — first direct, then recursive search by basename
		local base="${raw##*/}"
		for root in "$PWD" "/mnt/blue/S&box Projects" "$HOME/S&box Projects" "$ROOT" "$GAME_DIR"; do
			if [ -f "$root/$raw" ]; then
				found="$root/$raw"
				if command -v realpath >/dev/null 2>&1; then
					found="$(realpath "$found" 2>/dev/null || echo "$found")"
				else
					found="$(readlink -f "$found" 2>/dev/null || echo "$found")"
				fi
				echo "$found"
				return 0
			fi
			# Recursive search for basename (handles 'bloodsigil.sbproj' → 'bloodsigil/bloodsigil.sbproj')
			[ -d "$root" ] || continue
			found="$(find "$root" -maxdepth 4 -name "$base" -print -quit 2>/dev/null || true)"
			if [ -n "$found" ]; then
				if command -v realpath >/dev/null 2>&1; then
					found="$(realpath "$found" 2>/dev/null || echo "$found")"
				else
					found="$(readlink -f "$found" 2>/dev/null || echo "$found")"
				fi
				echo "$found"
				return 0
			fi
		done
		echo "error: .sbproj not found: $raw" >&2
		return 1
	fi

	# 3) Directory containing a .sbproj
	if [ -d "$raw" ]; then
		found="$(find "$raw" -maxdepth 1 -name "*.sbproj" -print -quit 2>/dev/null || true)"
		if [ -n "$found" ]; then
			if command -v realpath >/dev/null 2>&1; then
				found="$(realpath "$found" 2>/dev/null || echo "$found")"
			else
				found="$(readlink -f "$found" 2>/dev/null || echo "$found")"
			fi
			echo "$found"
			return 0
		fi
		# Also try raw/.sbproj convention
		if [ -f "$raw/.sbproj" ]; then
			if command -v realpath >/dev/null 2>&1; then
				echo "$(realpath "$raw/.sbproj" 2>/dev/null || echo "$raw/.sbproj")"
			else
				echo "$(readlink -f "$raw/.sbproj" 2>/dev/null || echo "$raw/.sbproj")"
			fi
			return 0
		fi
	fi

	# 4) Package ident <org>.<ident> (e.g. fss.bloodsigil) — search for matching .sbproj by Org/Ident
	if [[ "$raw" == *.* ]] && [[ "$raw" != */* ]] && [[ "$raw" != *\ * ]]; then
		local org="${raw%%.*}"
		local ident="${raw##*.}"
		# Strip possible #local suffix
		ident="${ident%%#*}"
		for root in "/mnt/blue/S&box Projects" "$HOME/S&box Projects" "$ROOT/game/addons" "$ROOT" "$GAME_DIR" "$PWD"; do
			[ -d "$root" ] || continue
			while IFS= read -r -d '' f; do
				# Match both Org and Ident in the .sbproj JSON (whitespace tolerant)
				if grep -q "\"Org\"[[:space:]]*:[[:space:]]*\"$org\"" "$f" 2>/dev/null \
				   && grep -q "\"Ident\"[[:space:]]*:[[:space:]]*\"$ident\"" "$f" 2>/dev/null; then
					if command -v realpath >/dev/null 2>&1; then
						echo "$(realpath "$f" 2>/dev/null || echo "$f")"
					else
						echo "$(readlink -f "$f" 2>/dev/null || echo "$f")"
					fi
					return 0
				fi
			done < <(find "$root" -maxdepth 4 -name "*.sbproj" -print0 2>/dev/null)
			# Fallback: folder name equals ident
			if [ -f "$root/$ident/$ident.sbproj" ]; then
				found="$root/$ident/$ident.sbproj"
				if command -v realpath >/dev/null 2>&1; then
					echo "$(realpath "$found" 2>/dev/null || echo "$found")"
				else
					echo "$(readlink -f "$found" 2>/dev/null || echo "$found")"
				fi
				return 0
			fi
		done
		echo "error: package ident not found: $raw (searched S&box Projects, game/addons, ROOT)" >&2
		return 1
	fi

	# 5) Fallback — treat as path and try to resolve
	if [ -e "$raw" ]; then
		if command -v realpath >/dev/null 2>&1; then
			echo "$(realpath "$raw" 2>/dev/null || echo "$raw")"
		else
			echo "$(readlink -f "$raw" 2>/dev/null || echo "$raw")"
		fi
		return 0
	fi
	echo "error: cannot resolve -project value: $raw" >&2
	return 1
}

# Build normalized arg list, resolving -project values
ARGS=()
need_project_value=0
for arg in "$@"; do
	if [ "$need_project_value" = 1 ]; then
		resolved="$(resolve_project_arg "$arg" || true)"
		if [ -z "$resolved" ]; then
			exit 1
		fi
		ARGS+=("$resolved")
		need_project_value=0
		continue
	fi
	if [ "$arg" = "-project" ] || [ "$arg" = "--project" ]; then
		ARGS+=("$arg")
		need_project_value=1
		continue
	fi
	ARGS+=("$arg")
done
if [ "$need_project_value" = 1 ]; then
	echo "error: -project requires a value (<org>.<ident> or path to .sbproj)" >&2
	exit 1
fi

# Also handle --print-env: show what would be executed with resolved args
if [ "${ARGS[0]:-}" = "--print-env" ]; then
	echo "LD_PRELOAD=$LD_PRELOAD"
	echo "LD_LIBRARY_PATH=$LD_LIBRARY_PATH"
	echo "QT_QPA_PLATFORM=$QT_QPA_PLATFORM"
	if [ ${#ARGS[@]} -gt 1 ]; then
		echo "exec $EXE ${ARGS[@]:1}"
	else
		echo "exec $EXE"
	fi
	exit 0
fi

# The engine resolves content paths relative to the working directory.
cd "$GAME_DIR"
exec "$EXE" "${ARGS[@]}"
