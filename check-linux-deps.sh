#!/usr/bin/env bash
#
# check-linux-deps.sh -- dependency report for the native Linux binaries
# shipped in game/bin/linuxsteamrt64.
#
# Run it from the sbox-public repo root:
#
#     ./check-linux-deps.sh
#
# To capture a report to share:
#
#     ./check-linux-deps.sh --no-color > deps-report.txt 2>&1
#
# It reports, and only reports -- it installs nothing and changes nothing:
#
#   * symbol-version mismatches (the "version `GLIBCXX_3.4.30' not found"
#     class of failure)
#   * how much symbol-version headroom this host has, per system library
#   * the full set of system libraries this build reaches outside its own
#     directory
#   * shared libraries the dynamic loader cannot find, and which binaries
#     need them -- reported last, so it is what you are left looking at
#
# Exit status:
#   0  every dependency resolved
#   1  something is missing or a version is unsatisfiable
#   2  the report could not be produced (wrong directory, no ldd, old bash)

set -uo pipefail

BIN_SUBDIR="game/bin/linuxsteamrt64"
VERBOSE=0
USE_COLOR=auto
BIN_DIR=""

usage()
{
	cat <<'USAGE'
Usage: ./check-linux-deps.sh [options]

Reports missing shared-library dependencies and symbol-version problems for
the native Linux binaries in game/bin/linuxsteamrt64.

Options:
  -d, --dir PATH   Report on PATH instead of <repo root>/game/bin/linuxsteamrt64
  -v, --verbose    Add a per-binary dependency listing
      --no-color   Disable coloured output (use this when saving to a file)
  -h, --help       Show this help

Exit status: 0 = all dependencies resolved, 1 = problems found, 2 = the
report could not be produced.
USAGE
}

while [ $# -gt 0 ]; do
	case "$1" in
		-d|--dir)
			if [ $# -lt 2 ]; then echo "--dir needs a path" >&2; exit 2; fi
			BIN_DIR="$2"; shift 2 ;;
		-v|--verbose)  VERBOSE=1; shift ;;
		--no-color)    USE_COLOR=never; shift ;;
		-h|--help)     usage; exit 0 ;;
		*)             echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
	esac
done

# ---------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------

if [ "${BASH_VERSINFO[0]:-0}" -lt 4 ]; then
	echo "This script needs bash 4 or newer (found ${BASH_VERSION:-unknown})." >&2
	exit 2
fi

if ! command -v ldd >/dev/null 2>&1; then
	echo "ldd was not found on PATH -- it ships in glibc's libc-bin package." >&2
	exit 2
fi

# objdump gives the symbol-version analysis. Without it the rest still works.
HAVE_OBJDUMP=0
command -v objdump >/dev/null 2>&1 && HAVE_OBJDUMP=1

# Locate the binary directory: explicit --dir, else walk up from the script's
# own location (normally the repo root), else up from the current directory.
if [ -z "$BIN_DIR" ]; then
	self_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)
	for start in "$self_dir" "$PWD"; do
		dir=$start
		while [ -n "$dir" ] && [ "$dir" != "/" ]; do
			if [ -d "$dir/$BIN_SUBDIR" ]; then BIN_DIR="$dir/$BIN_SUBDIR"; break 2; fi
			dir=$(dirname -- "$dir")
		done
	done
fi

if [ -z "$BIN_DIR" ] || [ ! -d "$BIN_DIR" ]; then
	echo "Could not find $BIN_SUBDIR." >&2
	echo "Run this from the sbox-public repo root, or pass --dir <path>." >&2
	exit 2
fi

BIN_DIR=$(cd -- "$BIN_DIR" && pwd)

if [ "$USE_COLOR" = auto ] && [ -t 1 ] && [ -z "${NO_COLOR-}" ]; then
	USE_COLOR=always
fi

if [ "$USE_COLOR" = always ]; then
	C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
	C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_CYAN=$'\033[36m'
else
	C_RESET=''; C_BOLD=''; C_DIM=''
	C_RED=''; C_GREEN=''; C_YELLOW=''; C_CYAN=''
fi

hr() { printf '%s\n' "${C_DIM}--------------------------------------------------------------------------${C_RESET}"; }

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

is_elf()
{
	local magic
	magic=$(head -c 4 -- "$1" 2>/dev/null | od -An -tx1 2>/dev/null | tr -d ' \n')
	[ "$magic" = "7f454c46" ]
}

# Strip a trailing soname version: libfoo.so.5.15.2 -> libfoo.so
soname_stem()
{
	printf '%s' "$1" | sed -E 's/\.so(\.[0-9]+)*$/.so/'
}

# True when $1 >= $2 as a dotted version.
ver_ge()
{
	[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}

# Highest version of family $2 (GLIBC, GLIBCXX, CXXABI, GCC) that the library
# at $1 defines.
provided_max()
{
	[ "$HAVE_OBJDUMP" -eq 1 ] || return 1
	objdump -p -- "$1" 2>/dev/null \
		| awk '/^Version definitions:/ {on=1} on' \
		| grep -oE "\\b$2_[0-9][0-9A-Za-z.]*" \
		| sort -V | tail -1
}

# Every "required from <lib>: <VERSION>" pair in the binary at $1.
version_needs()
{
	[ "$HAVE_OBJDUMP" -eq 1 ] || return 1
	objdump -p -- "$1" 2>/dev/null | awk '
		/^Version References:/ { inref = 1; next }
		/^[^ ]/                { inref = 0 }
		inref && /^  required from / { lib = $3; sub( /:$/, "", lib ); next }
		inref && lib != "" && NF >= 4 { print lib, $NF }
	'
}

# ---------------------------------------------------------------------------
# Scan
# ---------------------------------------------------------------------------

declare -A SEEN_REAL=()      # realpath        -> canonical display name
declare -A SEEN_COPY=()      # dir|stem|size   -> canonical display name
declare -A ALIASES=()        # display name    -> " alias alias"
declare -A MISSING_FOR=()    # display name    -> " soname soname"
declare -A CONSUMERS=()      # missing soname  -> " binary binary"
declare -A RESOLVED=()       # soname          -> resolved path
declare -A DEPS_FOR=()       # display name    -> " soname soname"
declare -A REQ_VER=()        # soname|FAMILY   -> highest version required
declare -A REQ_BY=()         # soname|FAMILY   -> binary requiring it
declare -a VERSION_ERRORS=()
declare -a LDD_ERRORS=()
declare -a CLEAN=()

total=0; duplicates=0; nonelf=0; checked=0; static=0

while IFS= read -r -d '' path; do
	total=$(( total + 1 ))
	rel=${path#"$BIN_DIR"/}

	real=$(readlink -f -- "$path" 2>/dev/null) || real="$path"
	if [ -n "${SEEN_REAL[$real]-}" ]; then
		canon=${SEEN_REAL[$real]}
		ALIASES[$canon]="${ALIASES[$canon]-} $rel"
		duplicates=$(( duplicates + 1 ))
		continue
	fi

	if [ ! -r "$path" ]; then
		LDD_ERRORS+=( "$rel: not readable" )
		continue
	fi

	if ! is_elf "$path"; then
		nonelf=$(( nonelf + 1 ))
		continue
	fi

	# The versioned soname families ship as full copies rather than symlinks
	# (libQt5Core.so, .so.5, .so.5.15, .so.5.15.2 are four identical files).
	# Same directory, same stem, same size means the same binary -- check it
	# once and list the rest as aliases.
	size=$(stat -c %s -- "$path" 2>/dev/null || echo 0)
	copykey="$(dirname -- "$rel")|$(soname_stem "$(basename -- "$rel")")|$size"
	if [ -n "${SEEN_COPY[$copykey]-}" ]; then
		canon=${SEEN_COPY[$copykey]}
		ALIASES[$canon]="${ALIASES[$canon]-} $rel"
		duplicates=$(( duplicates + 1 ))
		continue
	fi

	SEEN_REAL[$real]="$rel"
	SEEN_COPY[$copykey]="$rel"
	checked=$(( checked + 1 ))

	out=$(ldd -- "$path" 2>&1)

	case "$out" in
		*"not a dynamic executable"*|*"statically linked"*)
			static=$(( static + 1 ))
			continue ;;
	esac

	missing=''; deps=''
	while IFS= read -r line; do
		case "$line" in
			*"=> not found"*)
				lib=${line#"${line%%[![:space:]]*}"}
				lib=${lib%% *}
				missing="$missing $lib"
				deps="$deps $lib"
				CONSUMERS[$lib]="${CONSUMERS[$lib]-} $rel"
				;;
			*" => "*)
				lib=${line#"${line%%[![:space:]]*}"}
				lib=${lib%% *}
				target=${line#*" => "}
				target=${target% (*}
				deps="$deps $lib"
				[ -n "${RESOLVED[$lib]-}" ] || RESOLVED[$lib]="$target"
				;;
			*"version \`"*"' not found"*)
				short=${line//"$BIN_DIR"\//}
				VERSION_ERRORS+=( "$rel: ${short#*: }" )
				;;
			*"error while loading"*|*"cannot open shared object"*)
				LDD_ERRORS+=( "$rel: ${line# }" )
				;;
		esac
	done <<< "$out"

	DEPS_FOR[$rel]="$deps"

	if [ -n "$missing" ]; then
		MISSING_FOR[$rel]="$missing"
	else
		CLEAN+=( "$rel" )
	fi

	# Highest symbol version this binary asks of each library it links against.
	if [ "$HAVE_OBJDUMP" -eq 1 ]; then
		while read -r vlib vname; do
			[ -n "$vname" ] || continue
			family=${vname%_*}
			version=${vname##*_}
			case "$version" in
				[0-9]*) ;;
				*) continue ;;
			esac
			key="$vlib|$family"
			if [ -z "${REQ_VER[$key]-}" ] || ! ver_ge "${REQ_VER[$key]}" "$version"; then
				REQ_VER[$key]="$version"
				REQ_BY[$key]="$rel"
			fi
		done < <( version_needs "$path" )
	fi
done < <( find "$BIN_DIR" \( -type f -o -type l \) -print0 | sort -z )

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

distro="unknown"
[ -r /etc/os-release ] && distro=$( . /etc/os-release 2>/dev/null; printf '%s' "${PRETTY_NAME:-${NAME:-unknown}}" )
glibc=$( getconf GNU_LIBC_VERSION 2>/dev/null || ldd --version 2>/dev/null | head -1 )

printf '%s\n' "${C_BOLD}s&box native Linux dependency report${C_RESET}"
hr
printf '  directory  : %s\n' "$BIN_DIR"
printf '  distro     : %s\n' "$distro"
printf '  kernel     : %s\n' "$(uname -srm)"
printf '  libc       : %s\n' "$glibc"
printf '  date       : %s\n' "$(date '+%Y-%m-%d %H:%M:%S %Z')"
printf '  scanned    : %d files -- %d ELF binaries checked (%d with no dynamic deps)\n' \
	"$total" "$checked" "$static"
printf '               %d identical version-suffixed copies collapsed, %d non-ELF files ignored\n' \
	"$duplicates" "$nonelf"
if [ "$HAVE_OBJDUMP" -eq 0 ]; then
	printf '  %snote: objdump not installed -- skipping the symbol-version sections%s\n' "$C_YELLOW" "$C_RESET"
fi
printf '\n'

# 1. Version mismatches -----------------------------------------------------

printf '%s\n' "${C_BOLD}1. Unsatisfiable symbol versions${C_RESET}"
hr
if [ ${#VERSION_ERRORS[@]} -eq 0 ]; then
	printf '  %sNone -- every symbol version the binaries ask for is present.%s\n' "$C_GREEN" "$C_RESET"
else
	printf '  %sThe library was found but is older than the binary requires.%s\n' "$C_DIM" "$C_RESET"
	for issue in "${VERSION_ERRORS[@]}"; do
		printf '  %s%s%s\n' "$C_RED" "$issue" "$C_RESET"
	done
fi
if [ ${#LDD_ERRORS[@]} -gt 0 ]; then
	printf '\n  %sOther loader errors:%s\n' "$C_YELLOW" "$C_RESET"
	for err in "${LDD_ERRORS[@]}"; do
		printf '    %s\n' "$err"
	done
fi
printf '\n'

# 2. Symbol-version headroom ------------------------------------------------

version_problems=0

if [ "$HAVE_OBJDUMP" -eq 1 ]; then
	printf '%s\n' "${C_BOLD}2. Symbol-version headroom${C_RESET}"
	hr
	printf '  %sHighest version each system library is asked for, against what this host has.%s\n' \
		"$C_DIM" "$C_RESET"
	printf '  %sA TOO OLD row means this host cannot run that binary as shipped.%s\n' \
		"$C_DIM" "$C_RESET"
	printf '  %-22s %-9s %-10s %-10s %s\n' "LIBRARY" "FAMILY" "REQUIRED" "HOST HAS" "STATUS"

	shown=0
	for key in $( printf '%s\n' "${!REQ_VER[@]}" | sort ); do
		vlib=${key%%|*}
		family=${key##*|}
		required=${REQ_VER[$key]}
		hostlib=${RESOLVED[$vlib]-}

		# Only system libraries matter here -- anything resolving inside the
		# shipped directory travels with the build.
		case "$hostlib" in
			"$BIN_DIR"/*|"") continue ;;
		esac

		have=$( provided_max "$hostlib" "$family" )
		have=${have#"${family}_"}
		shown=$(( shown + 1 ))

		if [ -z "$have" ]; then
			status="${C_YELLOW}unknown${C_RESET}"; have="?"
		elif ver_ge "$have" "$required"; then
			status="${C_GREEN}ok${C_RESET}"
		else
			status="${C_RED}TOO OLD${C_RESET}"
			version_problems=$(( version_problems + 1 ))
		fi

		printf '  %-22s %-9s %-10s %-10s %b\n' "$vlib" "$family" "$required" "$have" "$status"
		if [ "$VERBOSE" -eq 1 ]; then
			printf '      %shighest demand comes from %s%s\n' "$C_DIM" "${REQ_BY[$key]}" "$C_RESET"
		fi
	done

	[ "$shown" -eq 0 ] && printf '  %sNo system libraries with versioned symbols.%s\n' "$C_DIM" "$C_RESET"
	printf '\n'
fi

# 3. External dependency surface --------------------------------------------

printf '%s\n' "${C_BOLD}3. System libraries this build reaches for${C_RESET}"
hr
bundled=0; external=0
for lib in $( printf '%s\n' "${!RESOLVED[@]}" | sort ); do
	target=${RESOLVED[$lib]}
	case "$target" in
		"$BIN_DIR"/*) bundled=$(( bundled + 1 )); continue ;;
	esac
	case "$lib" in
		linux-vdso.so*|/lib64/ld-linux*|ld-linux*) continue ;;
	esac
	external=$(( external + 1 ))
	printf '  %-32s %s%s%s\n' "$lib" "$C_DIM" "$target" "$C_RESET"
done
printf '\n  %d soname%s from the system, %d bundled in the binary directory.\n' \
	"$external" "$( [ "$external" -eq 1 ] && echo '' || echo s )" "$bundled"
printf '\n'

# 4. Per-binary detail (verbose) --------------------------------------------

if [ "$VERBOSE" -eq 1 ]; then
	printf '%s\n' "${C_BOLD}4. Per-binary detail${C_RESET}"
	hr
	for bin in $( printf '%s\n' "${!DEPS_FOR[@]}" | sort ); do
		# shellcheck disable=SC2086
		set -- ${DEPS_FOR[$bin]}
		if [ -n "${MISSING_FOR[$bin]-}" ]; then
			miss=${MISSING_FOR[$bin]}
			printf '  %s%s%s -- %d deps, missing %s%s%s\n' "$C_YELLOW" "$bin" "$C_RESET" \
				"$#" "$C_RED" "${miss# }" "$C_RESET"
		else
			printf '  %s -- %d deps, all resolved\n' "$bin" "$#"
		fi
		if [ -n "${ALIASES[$bin]-}" ]; then
			printf '      %sidentical copies:%s%s\n' "$C_DIM" "${ALIASES[$bin]}" "$C_RESET"
		fi
	done
	printf '\n'
fi

# Missing libraries ---------------------------------------------------------
#
# Reported last, and in place of the usual summary block: this is the part
# that has to be seen, so it is what the report ends on.

hr
if [ ${#CONSUMERS[@]} -gt 0 ]; then
	printf '%s\n' "${C_RED}${C_BOLD}MISSING LIBRARIES${C_RESET}"
	hr
	for lib in $( printf '%s\n' "${!CONSUMERS[@]}" | sort ); do
		# shellcheck disable=SC2086
		set -- ${CONSUMERS[$lib]}
		printf '  %s%s%s\n' "$C_RED" "$lib" "$C_RESET"
		printf '      needed by %d binar%s:\n' "$#" "$( [ $# -eq 1 ] && echo y || echo ies )"
		for bin in "$@"; do
			printf '        %s\n' "$bin"
		done
	done
	printf '\n  %d missing librar%s, affecting %d of %d binar%s.\n' \
		"${#CONSUMERS[@]}" "$( [ ${#CONSUMERS[@]} -eq 1 ] && echo y || echo ies )" \
		"${#MISSING_FOR[@]}" "$checked" \
		"$( [ "$checked" -eq 1 ] && echo y || echo ies )"
else
	printf '%s\n' "${C_GREEN}${C_BOLD}NO MISSING LIBRARIES${C_RESET}"
	hr
	printf '  All %d binar%s resolved every dependency on this host.\n' \
		"$checked" "$( [ "$checked" -eq 1 ] && echo y || echo ies )"
fi

# Problems from the sections above, carried down so the tail of the report is
# the whole verdict.
if [ ${#VERSION_ERRORS[@]} -gt 0 ] || [ "$version_problems" -gt 0 ] || [ ${#LDD_ERRORS[@]} -gt 0 ]; then
	printf '\n  %sAlso reported above:%s\n' "$C_YELLOW" "$C_RESET"
	[ ${#VERSION_ERRORS[@]} -gt 0 ] && \
		printf '    %d unsatisfiable symbol version%s (section 1)\n' \
			"${#VERSION_ERRORS[@]}" "$( [ ${#VERSION_ERRORS[@]} -eq 1 ] && echo '' || echo s )"
	[ ${#LDD_ERRORS[@]} -gt 0 ] && \
		printf '    %d loader error%s (section 1)\n' \
			"${#LDD_ERRORS[@]}" "$( [ ${#LDD_ERRORS[@]} -eq 1 ] && echo '' || echo s )"
	[ "$version_problems" -gt 0 ] && \
		printf '    %d system librar%s older than required (section 2)\n' \
			"$version_problems" "$( [ "$version_problems" -eq 1 ] && echo y || echo ies )"
fi

if [ ${#CONSUMERS[@]} -eq 0 ] && [ ${#VERSION_ERRORS[@]} -eq 0 ] \
	&& [ "$version_problems" -eq 0 ] && [ ${#LDD_ERRORS[@]} -eq 0 ]; then
	exit 0
fi
exit 1
