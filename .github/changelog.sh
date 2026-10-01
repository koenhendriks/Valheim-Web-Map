#!/usr/bin/env bash
# Prints Markdown release notes for a version: every commit since the previous v* tag, grouped by
# conventional-commit type. Usage: changelog.sh <version> [repo-url]
set -eu
version=$1
repo=${2:-}
prev=$(git tag --list 'v*' --sort=-v:refname | grep -vx "v$version" | head -n 1 || true)
range=${prev:+$prev..}HEAD

declare -A titles=(
  [feat]="Features" [fix]="Fixes" [perf]="Performance" [refactor]="Refactoring"
  [docs]="Documentation" [build]="Build" [ci]="CI" [test]="Tests" [chore]="Chores" [other]="Other changes"
)
order=(feat fix perf refactor docs build ci test chore other)
declare -A sections=()

conventional='^([a-z]+)(\([^)]*\))?(!)?: (.*)$'
while IFS=$'\t' read -r hash subject; do
  [ -n "$hash" ] || continue
  if [[ "$subject" =~ $conventional ]]; then
    type=${BASH_REMATCH[1]}
    scope=${BASH_REMATCH[2]}
    text=${BASH_REMATCH[4]}
    [[ -v "titles[$type]" ]] || type=other
    [ -n "$scope" ] && text="**${scope:1:-1}**: $text"
  else
    type=other
    text=$subject
  fi
  sections[$type]+="- $text (${hash})"$'\n'
done < <(git log --no-merges --pretty=format:'%h%x09%s' "$range")

if [ ${#sections[@]} -eq 0 ]; then
  echo "No changes since ${prev:-the beginning}."
  exit 0
fi

for type in "${order[@]}"; do
  [[ -v "sections[$type]" ]] || continue
  printf '## %s\n\n%s\n' "${titles[$type]}" "${sections[$type]}"
done

if [ -n "$repo" ]; then
  if [ -n "$prev" ]; then
    echo "**Full changelog**: $repo/compare/$prev...v$version"
  else
    echo "**Full changelog**: $repo/commits/v$version"
  fi
fi
