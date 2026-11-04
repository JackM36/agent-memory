#!/bin/sh
# Installs or updates the brain CLI on macOS and Linux from the GitHub release.
#
#   curl -fsSL https://raw.githubusercontent.com/JackM36/agent-memory/main/install.sh | sh
#
# Settings (environment variables):
#   AGENT_BRAIN_VERSION      a version such as 0.3.1 (default: the latest release)
#   AGENT_BRAIN_INSTALL_DIR  install folder (default: ~/.agent-brain/bin)
#   AGENT_BRAIN_REPOSITORY   owner/repo to download from (default: JackM36/agent-memory)
#   AGENT_BRAIN_BASE_URL     download folder instead of the GitHub release (mirrors, testing)
#   AGENT_BRAIN_NO_MODIFY_PATH  set to 1 to leave PATH alone
#
# The folder is added to PATH in your shell profile. Running the script again updates brain in place, so MCP
# registrations and hooks that call "brain" keep working. "brain upgrade" does the same from inside the tool.
set -eu

repository="${AGENT_BRAIN_REPOSITORY:-JackM36/agent-memory}"
install_dir="${AGENT_BRAIN_INSTALL_DIR:-$HOME/.agent-brain/bin}"
version="${AGENT_BRAIN_VERSION:-}"

fail() {
    echo "error: $*" >&2
    exit 1
}

case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    *) fail "unsupported system $(uname -s); on Windows use install.ps1" ;;
esac

case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    arm64 | aarch64) arch=arm64 ;;
    *) fail "unsupported architecture $(uname -m); build from source instead (see README)" ;;
esac

if [ -n "${AGENT_BRAIN_BASE_URL:-}" ]; then
    base="${AGENT_BRAIN_BASE_URL%/}"
elif [ -n "$version" ]; then
    base="https://github.com/$repository/releases/download/v${version#v}"
else
    base="https://github.com/$repository/releases/latest/download"
fi

download() {
    if command -v curl > /dev/null 2>&1; then
        curl -fsSL "$1" -o "$2"
    elif command -v wget > /dev/null 2>&1; then
        wget -q "$1" -O "$2"
    else
        fail "curl or wget is required"
    fi
}

sha256() {
    if command -v sha256sum > /dev/null 2>&1; then
        sha256sum "$1" | cut -d ' ' -f 1
    else
        shasum -a 256 "$1" | cut -d ' ' -f 1
    fi
}

asset="brain-$os-$arch.tar.gz"
temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT

echo "Downloading $asset from $base"
download "$base/$asset" "$temp/$asset"
download "$base/SHA256SUMS" "$temp/SHA256SUMS"

expected="$(awk -v name="$asset" '{ file = $2; sub(/^\*/, "", file); if (file == name) { print $1; exit } }' "$temp/SHA256SUMS")"
[ -n "$expected" ] || fail "SHA256SUMS has no entry for $asset"
[ "$(sha256 "$temp/$asset")" = "$expected" ] || fail "checksum mismatch for $asset; nothing was installed"

mkdir -p "$temp/unpacked" "$install_dir"
tar -xzf "$temp/$asset" -C "$temp/unpacked"

# Renaming over a running binary is safe on Unix: running agents keep the old file until they restart.
for file in "$temp"/unpacked/*; do
    mv -f "$file" "$install_dir/$(basename "$file")"
done

chmod +x "$install_dir/brain"
if [ "$os" = osx ]; then
    xattr -d com.apple.quarantine "$install_dir/brain" 2> /dev/null || true
fi

case ":$PATH:" in
    *":$install_dir:"*) ;;
    *)
        if [ -n "${AGENT_BRAIN_NO_MODIFY_PATH:-}" ]; then
            echo "Add $install_dir to PATH to use brain."
        else
            line="export PATH=\"$install_dir:\$PATH\""
            for profile in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.profile"; do
                if [ -f "$profile" ] && ! grep -Fq "$install_dir" "$profile"; then
                    printf '\n# Agent Brain\n%s\n' "$line" >> "$profile"
                    echo "Added $install_dir to PATH in $profile (open a new terminal to pick it up)."
                fi
            done
        fi
        ;;
esac

echo "Installed brain $("$install_dir/brain" --version) in $install_dir"
echo "Next: cd into a project and run 'brain init'. Upgrade later with 'brain upgrade'."
