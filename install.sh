#!/usr/bin/env bash
# Glacier CLI Automated Installer for Linux and macOS
set -euo pipefail

INSTALL_DIR="${HOME}/.glacier/bin"
EXE_TARGET="${INSTALL_DIR}/glacier"

UNAME_S="$(uname -s)"
UNAME_M="$(uname -m)"

case "${UNAME_S}" in
    Linux*)  OS_ID="linux" ;;
    Darwin*) OS_ID="osx" ;;
    *)
        echo "Unsupported OS '${UNAME_S}'. Falling back to dotnet tool install..."
        dotnet tool update -g Glacier.Cli || dotnet tool install -g Glacier.Cli
        exit 0
        ;;
esac

case "${UNAME_M}" in
    x86_64|amd64)  ARCH_ID="x64" ;;
    aarch64|arm64) ARCH_ID="arm64" ;;
    *)
        echo "Unsupported architecture '${UNAME_M}'. Falling back to dotnet tool install..."
        dotnet tool update -g Glacier.Cli || dotnet tool install -g Glacier.Cli
        exit 0
        ;;
esac

DOWNLOAD_URL="https://github.com/ian-cowley/Glacier.Cli/releases/latest/download/glacier-${OS_ID}-${ARCH_ID}.tar.gz"

echo "======================================================================"
echo "  GLACIER CLI: Pure C# .NET 10 AI Platform (${OS_ID}-${ARCH_ID})"
echo "======================================================================"

mkdir -p "${INSTALL_DIR}"

echo ""
echo "[1/3] Downloading latest Glacier CLI..."
TMP_TAR=$(mktemp /tmp/glacier.XXXXXX.tar.gz)
trap 'rm -f "${TMP_TAR}"' EXIT

if ! curl -fsSL "${DOWNLOAD_URL}" -o "${TMP_TAR}"; then
    echo "Release archive not available for ${OS_ID}-${ARCH_ID}. Installing via .NET Global Tool..."
    dotnet tool update -g Glacier.Cli || dotnet tool install -g Glacier.Cli
    exit 0
fi

tar -xzf "${TMP_TAR}" -C "${INSTALL_DIR}"
chmod +x "${EXE_TARGET}"

echo "[2/3] Configuring PATH in shell profiles..."
SHELL_CONFIG=""
if [ -n "${ZSH_VERSION:-}" ] || [ -f "$HOME/.zshrc" ]; then
    SHELL_CONFIG="$HOME/.zshrc"
elif [ -n "${BASH_VERSION:-}" ] || [ -f "$HOME/.bashrc" ]; then
    SHELL_CONFIG="$HOME/.bashrc"
fi

if [ -n "$SHELL_CONFIG" ]; then
    if ! grep -q 'glacier/bin' "$SHELL_CONFIG"; then
        echo 'export PATH="$HOME/.glacier/bin:$PATH"' >> "$SHELL_CONFIG"
        echo "  ✓ Added ~/.glacier/bin to $SHELL_CONFIG"
    fi
fi

echo "[3/3] Verifying installation..."
export PATH="$HOME/.glacier/bin:$PATH"
"${EXE_TARGET}" --help > /dev/null

echo "======================================================================"
echo "  SUCCESS: Glacier CLI is installed to ${EXE_TARGET}"
echo "======================================================================"
echo "Quickstart: restart your terminal, then run:"
echo "  glacier devices"
echo "  glacier run <model.gguf> 'Hello World'"
echo "  glacier serve <model.gguf> --port 11434"
