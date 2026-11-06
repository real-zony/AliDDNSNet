#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ! "$1" =~ ^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
    printf 'Usage: %s <version, e.g. v0.1.0> [output-parent-directory]\n' "$0" >&2
    exit 2
fi

release_version=$1
application_version=${release_version#v}
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
application_project="$repository_root/AliCloudDynamicDNS/AliCloudDynamicDNS.csproj"
output_parent=${2:-"$repository_root/TempFiles"}
mkdir -p -- "$output_parent"
output_parent=$(cd -- "$output_parent" && pwd)
artifact_directory=$(mktemp -d "$output_parent/release-${release_version}.XXXXXXXX")
cd -- "$repository_root"
platforms=('win-x64' 'linux-x64' 'osx-x64' 'linux-arm' 'linux-arm64')

for platform in "${platforms[@]}"; do
    publish_directory="$artifact_directory/.publish/$platform"
    dotnet publish "$application_project" \
        --configuration Release --framework net10.0 --runtime "$platform" \
        --output "$publish_directory" --self-contained true \
        -p:Version="$application_version" -p:RuntimeFrameworkVersion=10.0.14 \
        -p:UseAppHost=true -p:PublishSingleFile=true -p:PublishTrimmed=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:ReleaseArchiveName="AliCloudDynamicDNS_${platform}_${release_version}.zip"
done

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf 'artifact_dir=%s\n' "$artifact_directory" >> "$GITHUB_OUTPUT"
fi
printf 'Release archives: %s\n' "$artifact_directory"
