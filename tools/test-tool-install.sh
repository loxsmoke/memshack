#!/usr/bin/env bash

set -euo pipefail

CONFIGURATION="Release"
SKIP_PACK=0

GREEN=$'\033[1;32m'
RED=$'\033[1;31m'
RESET=$'\033[0m'

write_status_frame() {
    local text="$1"
    local color="$2"
    local horizontal=""
    local width=$(( ${#text} + 2 ))

    for ((i = 0; i < width; i++)); do
        horizontal+=$'\u2550'
    done

    printf '\n%s\u2554%s\u2557%s\n' "$color" "$horizontal" "$RESET"
    printf '%s\u2551 %s \u2551%s\n' "$color" "$text" "$RESET"
    printf '%s\u255a%s\u255d%s\n' "$color" "$horizontal" "$RESET"
}

abort() {
    local message="$1"
    write_status_frame "Tool packaging smoke tests failed: $message" "$RED"
    exit 1
}

invoke_checked() {
    local label="$1"
    shift

    printf '==> %s\n' "$label"
    if ! "$@"; then
        abort "Step failed: $label"
    fi
}

get_project_version() {
    local project_path="$1"
    local version

    version="$(sed -n 's:.*<PackageVersion>\(.*\)</PackageVersion>.*:\1:p' "$project_path" | head -n 1)"
    if [[ -z "$version" ]]; then
        version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project_path" | head -n 1)"
    fi

    if [[ -z "$version" ]]; then
        abort "Could not determine package version from $project_path"
    fi

    printf '%s\n' "$version"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration)
            [[ $# -ge 2 ]] || abort "Missing value for --configuration"
            CONFIGURATION="$2"
            shift 2
            ;;
        --skip-pack)
            SKIP_PACK=1
            shift
            ;;
        *)
            abort "Unknown argument: $1"
            ;;
    esac
done

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
ARTIFACTS_ROOT="$REPO_ROOT/artifacts"
PACKAGE_OUTPUT="$REPO_ROOT/src/MemShack.Cli/nuget"
SMOKE_ROOT="$ARTIFACTS_ROOT/tool-smoke/$(date +%Y%m%d-%H%M%S)"
TOOL_PATH="$SMOKE_ROOT/tool-path"
HOME_ROOT="$SMOKE_ROOT/home"
PROJECT_ROOT="$SMOKE_ROOT/project"
PALACE_PATH="$SMOKE_ROOT/palace"
LOCAL_ROOT="$SMOKE_ROOT/local-manifest"
DOTNET_HOME="$REPO_ROOT/src/.dotnet"
NUGET_PACKAGES="$REPO_ROOT/src/.nuget/packages"
PROJECT_PATH="$REPO_ROOT/src/MemShack.Cli/MemShack.Cli.csproj"
VERSION="$(get_project_version "$PROJECT_PATH")"
PACKAGE_ID="LoxSmoke.Mems"
TOOL_COMMAND="mems"
TOOL_BIN="$TOOL_PATH/$TOOL_COMMAND"
PACKAGE_PATH="$PACKAGE_OUTPUT/$PACKAGE_ID.$VERSION.nupkg"

mkdir -p \
    "$ARTIFACTS_ROOT" \
    "$PACKAGE_OUTPUT" \
    "$SMOKE_ROOT" \
    "$TOOL_PATH" \
    "$HOME_ROOT" \
    "$PROJECT_ROOT/backend" \
    "$LOCAL_ROOT" \
    "$DOTNET_HOME" \
    "$NUGET_PACKAGES"

export DOTNET_CLI_HOME="$DOTNET_HOME"
export NUGET_PACKAGES
export HOME="$HOME_ROOT"
export USERPROFILE="$HOME_ROOT"

if [[ $SKIP_PACK -eq 0 ]]; then
    invoke_checked "Pack tool package" \
        dotnet pack "$PROJECT_PATH" -c "$CONFIGURATION"
fi

[[ -f "$PACKAGE_PATH" ]] || abort "Expected package was not found: $PACKAGE_PATH"

cat > "$PROJECT_ROOT/backend/memory-sample.txt" <<'EOF'
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
EOF

invoke_checked "Install tool with --tool-path" \
    dotnet tool install "$PACKAGE_ID" --tool-path "$TOOL_PATH" --add-source "$PACKAGE_OUTPUT" --version "$VERSION" --ignore-failed-sources

[[ -f "$TOOL_BIN" ]] || abort "Installed tool executable was not found: $TOOL_BIN"

if ! help_text="$("$TOOL_BIN" --help 2>&1)"; then
    abort "mems --help failed"
fi

[[ "$help_text" == *"mems init <dir>"* ]] || abort "Installed tool help output did not mention the mems command."

invoke_checked "Run mems init" \
    "$TOOL_BIN" init "$PROJECT_ROOT" --yes

invoke_checked "Run mems mine" \
    "$TOOL_BIN" --palace "$PALACE_PATH" mine "$PROJECT_ROOT"

if ! status_text="$("$TOOL_BIN" --palace "$PALACE_PATH" status 2>&1)"; then
    abort "mems status failed"
fi

[[ "$status_text" == *"WING: project"* ]] || abort "Installed tool status output did not include the expected wing."

if ! search_text="$("$TOOL_BIN" --palace "$PALACE_PATH" search "JWT authentication" 2>&1)"; then
    abort "mems search failed"
fi

[[ "$search_text" == *'Results for: "JWT authentication"'* ]] || abort "Installed tool search output did not include the expected result header."

pushd "$LOCAL_ROOT" > /dev/null
trap 'popd > /dev/null' RETURN

invoke_checked "Create local tool manifest" \
    dotnet new tool-manifest

invoke_checked "Install local tool" \
    dotnet tool install --local "$PACKAGE_ID" --add-source "$PACKAGE_OUTPUT" --version "$VERSION" --ignore-failed-sources

if ! local_list="$(dotnet tool list --local 2>&1)"; then
    abort "dotnet tool list --local failed"
fi

[[ "$local_list" == *"$PACKAGE_ID"* && "$local_list" == *"$TOOL_COMMAND"* ]] || abort "Local tool list did not contain the packaged tool."

invoke_checked "Run local tool" \
    dotnet tool run "$TOOL_COMMAND" -- --help

invoke_checked "Update local tool" \
    dotnet tool update --local "$PACKAGE_ID" --add-source "$PACKAGE_OUTPUT" --version "$VERSION" --ignore-failed-sources

invoke_checked "Uninstall local tool" \
    dotnet tool uninstall --local "$PACKAGE_ID"

trap - RETURN
popd > /dev/null

write_status_frame "Tool packaging smoke tests passed" "$GREEN"
printf 'Artifacts:\n'
printf '  Package: %s\n' "$PACKAGE_PATH"
printf '  Smoke root: %s\n' "$SMOKE_ROOT"
