#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
configuration="Release"
for_publication=false
private_config_directory=""

usage() {
    cat <<'EOF'
Usage: build-local-stack.sh [options]

Options:
  -c, --configuration NAME  Debug or Release (default: Release)
      --for-publication     Require embedded online service configuration
      --private-config-directory PATH  Directory containing presence.props and showcase.props
  -h, --help                Show this help
EOF
}

die() {
    echo "build-local-stack.sh: $*" >&2
    exit 2
}

while (($# > 0)); do
    case "$1" in
        -c|--configuration)
            (($# >= 2)) || die "missing value for $1"
            configuration="$2"
            shift 2
            ;;
        --for-publication)
            for_publication=true
            shift
            ;;
        --private-config-directory)
            (($# >= 2)) || die "missing value for $1"
            private_config_directory="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

case "$configuration" in
    Debug|Release) ;;
    *) die "configuration must be Debug or Release: $configuration" ;;
esac

command -v dotnet >/dev/null 2>&1 || {
    echo "build-local-stack.sh: dotnet is required" >&2
    exit 1
}

build_arguments=(
    build "$repository_root/CombatSolver.csproj"
    --configuration "$configuration"
    --nologo
)
if [[ "$for_publication" == true ]]; then
    build_arguments+=("-p:PublicationBuild=true")
    if [[ -z "$private_config_directory" ]]; then
        private_config_directory="$repository_root/.local"
    fi
    [[ -f "$private_config_directory/presence.props" ]] || die "missing private connection configuration: presence.props"
    [[ -f "$private_config_directory/showcase.props" ]] || die "missing private connection configuration: showcase.props"
    private_config_directory="$(cd -- "$private_config_directory" && pwd)"
    build_arguments+=(
        "-p:PresencePropsPath=$private_config_directory/presence.props"
        "-p:ShowcasePropsPath=$private_config_directory/showcase.props"
    )
fi

dotnet "${build_arguments[@]}"
