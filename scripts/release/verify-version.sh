#!/usr/bin/env bash
# Decides which version a release publishes and refuses anything that is not a clean SemVer 2.0.0 one.
#
#   verify-version.sh <tag> <project-file>
#
#   <tag>           the pushed git tag (v1.2.3, v1.0.0-rc.1...), or an empty string for a dry run that has no tag to check.
#   <project-file>  the .csproj whose <Version> is compiled into the tool.
#
# On success it prints exactly two lines, ready to be appended to $GITHUB_OUTPUT, and nothing else:
#   version=1.2.3
#   prerelease=false
# On failure it prints nothing on stdout, explains itself on stderr and exits 1 (64 when it is called wrongly).
#
# The tag is untrusted text: git allows characters such as $ ( ) ; and backticks in a tag name. It is only ever compared and
# printed through printf, never evaluated, and it is rejected unless it is strict SemVer, so it cannot add a line to the output.
#
# Kept compatible with bash 3.2 (the one macOS ships): no ${var@Q}, no associative arrays.

set -euo pipefail

# Make [a-z] mean ASCII letters only, whatever the caller's locale says.
export LC_ALL=C

# SemVer 2.0.0 without build metadata. No leading zeros in numbers, and numeric prerelease identifiers cannot have them either.
readonly IDENTIFIER='(0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)'
readonly SEMVER="^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-${IDENTIFIER}(\\.${IDENTIFIER})*)?\$"

readonly EXIT_REFUSED=1
readonly EXIT_USAGE=64

# printf %q renders any value on one line, with anything odd (spaces, quotes, newlines) escaped.
quote() { printf '%q' "$1"; }

fail() {
  printf 'error: %s\n' "$*" >&2
  exit "${EXIT_REFUSED}"
}

is_semver() { [[ $1 =~ ${SEMVER} ]]; }

if [[ $# -ne 2 ]]; then
  echo "usage: verify-version.sh <tag-or-empty-for-a-dry-run> <project-file>" >&2
  exit "${EXIT_USAGE}"
fi

tag=$1
project=$2

[[ -f ${project} ]] || fail "the project file $(quote "${project}") does not exist."

# The version compiled into the tool: the first <Version>...</Version> on one line, trimmed the way MSBuild trims it.
version=
# In a variable on purpose: an unquoted \< inside [[ =~ ]] would reach the regex engine, where GNU treats it as a word boundary.
readonly VERSION_ELEMENT='<Version>(.*)</Version>'
while IFS= read -r line || [[ -n ${line} ]]; do
  if [[ ${line} =~ ${VERSION_ELEMENT} ]]; then
    version=${BASH_REMATCH[1]}
    break
  fi
done < "${project}"
version=${version#"${version%%[![:space:]]*}"}
version=${version%"${version##*[![:space:]]}"}

if [[ -z ${version} ]]; then
  fail "no <Version> was found in $(quote "${project}"); set it to the SemVer version being released, for example <Version>1.2.3</Version>."
fi

is_semver "${version}" ||
  fail "<Version> in $(quote "${project}") is $(quote "${version}"), which is not a SemVer 2.0.0 version without build metadata (MAJOR.MINOR.PATCH with an optional -prerelease)."

if [[ -n ${tag} ]]; then
  [[ ${tag} == v* ]] ||
    fail "the tag $(quote "${tag}") does not start with 'v': releases are tagged vMAJOR.MINOR.PATCH (SemVer 2.0.0), for example v1.2.3 or v1.2.3-rc.1."

  tag_version=${tag#v}

  if [[ ${tag_version} == *+* ]] && is_semver "${tag_version%%+*}"; then
    fail "the tag $(quote "${tag}") carries build metadata (+...), which SemVer ignores when it compares versions, so two releases could not be told apart. Tag it without the +... part."
  fi

  is_semver "${tag_version}" ||
    fail "the tag $(quote "${tag}") is not strict SemVer 2.0.0: expected vMAJOR.MINOR.PATCH with an optional -prerelease and no leading zeros."

  [[ ${tag_version} == "${version}" ]] ||
    fail "the tag $(quote "${tag}") says ${tag_version} but <Version> in $(quote "${project}") is ${version}: set <Version> to ${tag_version} and commit it before tagging."
fi

prerelease=false
[[ ${version} == *-* ]] && prerelease=true

printf 'version=%s\nprerelease=%s\n' "${version}" "${prerelease}"
