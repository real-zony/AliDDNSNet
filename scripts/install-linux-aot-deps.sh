#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: bash scripts/install-linux-aot-deps.sh <linux-x64|linux-arm64|linux-arm>\n' >&2
  exit 2
fi

# These packages and the deb822 source layout are deliberately tied to the CI image.
. /etc/os-release
if [[ "$ID" != ubuntu || "$VERSION_ID" != 24.04 ]]; then
  printf 'The AOT dependency helper requires Ubuntu 24.04.\n' >&2
  exit 2
fi

rid=$1
host_arch=$(dpkg --print-architecture)
case "$rid:$host_arch" in
  linux-x64:amd64|linux-arm64:arm64)
    sudo apt-get update
    sudo apt-get install --yes --no-install-recommends clang llvm lld binutils zlib1g-dev libssl-dev libicu-dev
    ;;
  linux-arm:amd64)
    # Adding armhf without restricting existing sources makes Ubuntu's amd64 mirrors
    # request nonexistent armhf indexes. Cover both Ubuntu 24.04 deb822 stanzas and
    # any legacy/third-party .list entries before the first multiarch apt update.
    sudo python3 - <<'PY'
from pathlib import Path
import re

sources_dir = Path('/etc/apt/sources.list.d')
armhf_source = sources_dir / 'aliddns-armhf.sources'
for path in sources_dir.glob('*.sources'):
    if path == armhf_source:
        continue
    stanzas = []
    for stanza in re.split(r'\n\s*\n', path.read_text()):
        if re.search(r'^Types:', stanza, re.MULTILINE):
            # Deb822 fields can have indented continuation lines.
            stanza = re.sub(r'^Architectures(?:-Add|-Remove)?:[^\n]*(?:\n[ \t]+[^\n]*)*\n?', '', stanza, flags=re.MULTILINE)
            stanza = 'Architectures: amd64\n' + stanza
        stanzas.append(stanza)
    path.write_text('\n\n'.join(stanzas).rstrip() + '\n')

legacy_sources = [Path('/etc/apt/sources.list'), *sources_dir.glob('*.list')]
for path in legacy_sources:
    if not path.is_file():
        continue
    lines = []
    for line in path.read_text().splitlines():
        match = re.match(r'^(\s*deb(?:-src)?\s+)(?:\[([^]]*)\]\s+)?(.*)$', line)
        if match:
            options = [item for item in (match[2] or '').split() if not re.match(r'^arch(?:[+-])?=', item)]
            options.append('arch=amd64')
            line = match[1] + '[' + ' '.join(options) + '] ' + match[3]
        lines.append(line)
    path.write_text('\n'.join(lines) + '\n')

armhf_source.write_text('''Types: deb
URIs: http://ports.ubuntu.com/ubuntu-ports/
Suites: noble noble-updates noble-security
Components: main restricted universe multiverse
Architectures: armhf
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
''')
PY
    sudo dpkg --add-architecture armhf
    sudo apt-get update
    sudo apt-get install --yes --no-install-recommends \
      clang llvm lld gcc-arm-linux-gnueabihf binutils-arm-linux-gnueabihf qemu-user \
      libc6-dev:armhf zlib1g-dev:armhf libssl-dev:armhf libicu-dev:armhf
    # armhf multiarch packages, unlike the GCC-only cross sysroot, include ICU and
    # OpenSSL. The workflow uses qemu-arm -L / and an explicit target LD_LIBRARY_PATH.
    test -f /usr/lib/arm-linux-gnueabihf/ld-linux-armhf.so.3
    test -f /usr/lib/arm-linux-gnueabihf/libz.so
    test -f /usr/lib/arm-linux-gnueabihf/libssl.so
    test -f /usr/lib/arm-linux-gnueabihf/libicuuc.so
    ;;
  *)
    printf 'Unsupported target/host pair: %s/%s\n' "$rid" "$host_arch" >&2
    exit 2
    ;;
esac
