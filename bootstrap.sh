#!/bin/bash
# Must keep LF line endings or Linux won't execute it (enforced by .gitattributes)
#
# SboxBuild resolves every path from the current working directory, so this must
# run from the repo root regardless of where it was invoked from.
#
# Before building, this downloads the prebuilt native binaries and checks them
# against this host's shared libraries. Those binaries ship stripped and cannot
# be rebuilt here, so a library this distro doesn't have surfaces as a runtime
# failure long after the build has succeeded -- a miserable way to find out.
#
# The download has to come first. DownloadPublicArtifacts is the first step
# inside `sboxbuild build` (Steps/Build.cs:19) and writes into game/bin/, so
# checking before that would inspect last run's binaries, or on a fresh clone
# nothing at all. Pulling them here means the check sees what the build is
# about to use; the build's own download pass then finds everything current.
#
# Usage:
#   ./bootstrap.sh              fetch natives, check dependencies, then build
#   ./bootstrap.sh -y           don't prompt if dependencies are missing
#   ./bootstrap.sh --skip-deps  don't fetch or check, just build
set -e

# Resolve our own path before the cd, or --help reads $0 relative to the caller's
# directory and silently finds nothing (./bootstrap.sh works, ../bootstrap.sh does not).
SELF_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
SELF="$SELF_DIR/$(basename -- "$0")"
cd -- "$SELF_DIR"

sboxbuild() {
	dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- "$@"
}

BIN_DIR="game/bin/linuxsteamrt64"
ASSUME_YES=0
SKIP_DEPS=0

while [ $# -gt 0 ]; do
	case "$1" in
		-y|--yes)     ASSUME_YES=1; shift ;;
		--skip-deps)  SKIP_DEPS=1; shift ;;
		-h|--help)
			sed -n '7,21p' "$SELF" | sed 's/^# \?//'
			exit 0 ;;
		*) echo "Unknown option: $1" >&2; exit 2 ;;
	esac
done

if [ -t 1 ] && [ -z "${NO_COLOR-}" ]; then
	C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
	C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
else
	C_RESET=''; C_BOLD=''; C_DIM=''
	C_RED=''; C_GREEN=''; C_YELLOW=''
fi

hr() { printf '%s\n' "${C_DIM}--------------------------------------------------------------------------${C_RESET}"; }

# ---------------------------------------------------------------------------
# Native dependency check
#
# Returns 0 when everything resolves, 1 when something is missing or a symbol
# version is unsatisfiable, 2 when the check could not be run at all.
# ---------------------------------------------------------------------------

check_native_deps()
{
	if [ "${BASH_VERSINFO[0]:-0}" -lt 4 ]; then
		printf '  %sskipped: needs bash 4+ for the scan (found %s)%s\n' \
			"$C_YELLOW" "${BASH_VERSION:-unknown}" "$C_RESET"
		return 2
	fi

	if ! command -v ldd >/dev/null 2>&1; then
		printf '  %sskipped: ldd not on PATH -- it ships in glibc'"'"'s libc-bin package%s\n' \
			"$C_YELLOW" "$C_RESET"
		return 2
	fi

	if [ ! -d "$BIN_DIR" ]; then
		printf '  %sskipped: %s does not exist -- the fetch above did not produce it%s\n' \
			"$C_YELLOW" "$BIN_DIR" "$C_RESET"
		return 2
	fi

	local -A seen_real=() seen_copy=() consumers=()
	local -a version_errors=() ldd_errors=()
	local path rel real size copykey out line lib stem
	local checked=0 failed=0

	while IFS= read -r -d '' path; do
		rel=${path#"$BIN_DIR"/}

		# Symlinks and identical version-suffixed copies (libQt5Core.so, .so.5,
		# .so.5.15, .so.5.15.2 are four copies of one file) would otherwise be
		# reported four times over. Same real path, or same dir+stem+size, is
		# the same binary -- check it once.
		real=$(readlink -f -- "$path" 2>/dev/null) || real="$path"
		[ -n "${seen_real[$real]-}" ] && continue

		[ -r "$path" ] || { ldd_errors+=( "$rel: not readable" ); continue; }
		[ "$(head -c 4 -- "$path" 2>/dev/null | od -An -tx1 2>/dev/null | tr -d ' \n')" = "7f454c46" ] || continue

		size=$(stat -c %s -- "$path" 2>/dev/null || echo 0)
		stem=$(printf '%s' "$(basename -- "$rel")" | sed -E 's/\.so(\.[0-9]+)*$/.so/')
		copykey="$(dirname -- "$rel")|$stem|$size"
		[ -n "${seen_copy[$copykey]-}" ] && continue

		seen_real[$real]="$rel"
		seen_copy[$copykey]="$rel"

		out=$(ldd -- "$path" 2>&1)
		case "$out" in
			*"not a dynamic executable"*|*"statically linked"*) continue ;;
		esac

		checked=$(( checked + 1 ))

		local miss=''
		while IFS= read -r line; do
			case "$line" in
				*"=> not found"*)
					lib=${line#"${line%%[![:space:]]*}"}; lib=${lib%% *}
					miss="$miss $lib"
					consumers[$lib]="${consumers[$lib]-} $rel"
					;;
				*"version \`"*"' not found"*)
					version_errors+=( "$rel: ${line#*: }" )
					;;
				*"error while loading"*|*"cannot open shared object"*)
					ldd_errors+=( "$rel: ${line# }" )
					;;
			esac
		done <<< "$out"

		if [ -n "$miss" ]; then
			failed=$(( failed + 1 ))
			printf '  %sFAIL%s  %-34s %s%s%s\n' "$C_RED" "$C_RESET" "$rel" "$C_RED" "${miss# }" "$C_RESET"
		else
			printf '  %sOK%s    %s\n' "$C_GREEN" "$C_RESET" "$rel"
		fi
	done < <( find "$BIN_DIR" \( -type f -o -type l \) -print0 2>/dev/null | sort -z )

	if [ "$checked" -eq 0 ]; then
		printf '  %sskipped: no dynamically linked binaries found in %s%s\n' \
			"$C_YELLOW" "$BIN_DIR" "$C_RESET"
		return 2
	fi

	printf '\n  %d OK, %d with missing libraries.\n' "$(( checked - failed ))" "$failed"

	if [ ${#consumers[@]} -gt 0 ]; then
		printf '\n'
		hr
		printf '%s\n' "${C_RED}${C_BOLD}MISSING LIBRARIES${C_RESET}"
		hr
		for lib in $( printf '%s\n' "${!consumers[@]}" | sort ); do
			# shellcheck disable=SC2086
			set -- ${consumers[$lib]}
			printf '  %s%s%s  %sneeded by %d: %s%s\n' "$C_RED" "$lib" "$C_RESET" "$C_DIM" "$#" "$*" "$C_RESET"
		done
	fi

	if [ ${#version_errors[@]} -gt 0 ]; then
		printf '\n'
		hr
		printf '%s\n' "${C_RED}${C_BOLD}UNSATISFIABLE SYMBOL VERSIONS${C_RESET}"
		hr
		printf '  %sthe library is present but older than the binary needs%s\n' "$C_DIM" "$C_RESET"
		for line in "${version_errors[@]}"; do
			printf '  %s%s%s\n' "$C_RED" "$line" "$C_RESET"
		done
	fi

	if [ ${#ldd_errors[@]} -gt 0 ]; then
		printf '\n  %sloader errors:%s\n' "$C_YELLOW" "$C_RESET"
		for line in "${ldd_errors[@]}"; do
			printf '    %s\n' "$line"
		done
	fi

	[ ${#consumers[@]} -eq 0 ] && [ ${#version_errors[@]} -eq 0 ] && [ ${#ldd_errors[@]} -eq 0 ]
}

# ---------------------------------------------------------------------------

if [ "$SKIP_DEPS" -eq 0 ]; then
	# --native-only limits this to game/bin/, which is all the check cares about.
	printf '%s\n' "${C_BOLD}Fetching native binaries${C_RESET}"
	hr
	if ! sboxbuild download-public-artifacts --native-only; then
		printf '  %swarning: download failed -- checking whatever is already on disk%s\n' \
			"$C_YELLOW" "$C_RESET"
	fi
	printf '\n'

	printf '%s\n' "${C_BOLD}Checking native dependencies in $BIN_DIR${C_RESET}"
	hr

	deps_rc=0
	check_native_deps || deps_rc=$?

	if [ "$deps_rc" -eq 1 ]; then
		printf '\n  %sThese are prebuilt binaries that cannot be rebuilt here, so the managed build\n' "$C_YELLOW"
		printf '  below will still succeed -- but the editor will not run until they resolve.%s\n\n' "$C_RESET"

		if [ "$ASSUME_YES" -eq 1 ]; then
			echo "Continuing anyway (-y)."
		elif [ ! -t 0 ]; then
			echo "Not an interactive terminal, continuing anyway."
		else
			read -r -p "Continue with the build anyway? [y/N] " reply
			case "$reply" in
				[yY]|[yY][eE][sS]) ;;
				*) echo "Aborted."; exit 1 ;;
			esac
		fi
	fi
	printf '\n'
fi

sboxbuild build --config Developer

# build-shaders and build-content look for game/bin/managed/shadercompiler.exe and
# game/bin/win64/contentbuilder.exe, which don't exist on Linux (the native Linux
# contentbuilder lives in game/bin/linuxsteamrt64). Warn and continue rather than
# aborting the whole bootstrap - the managed build above is the part that works.
sboxbuild build-shaders || echo "warning: build-shaders failed (not supported on Linux yet), continuing"
sboxbuild build-content || echo "warning: build-content failed (not supported on Linux yet), continuing"
