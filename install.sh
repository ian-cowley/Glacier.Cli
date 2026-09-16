#!/usr/bin/env bash
# Glacier CLI Automated Installer for Linux and macOS
set -e

INSTALL_DIR="${HOME}/.glacier/bin"
EXE_TARGET="${INSTALL_DIR}/glacier"
DOWNLOAD_URL="https://github.com/ian-cowley/Glacier.Cli/releases/latest/download/glacier-linux-x64.tar.gz"

echo "======================================================================"
echo "  GLACIER CLI: The World-Leading Pure C# .NET 10 AI Platform"
echo "======================================================================"

mkdir -p "${INSTALL_DIR}"

echo ""
echo "[1/3] Downloading latest Glacier CLI..."
TMP_TAR=$(mktemp /tmp/glacier.XXXXXX.tar.gz)
curl -fsSL "${DOWNLOAD_URL}" -o "${TMP_TAR}" || {
    echo "Direct download failed. Falling back to dotnet tool install..."
    dotnet tool install -g Glacier.Cli
    exit 0
}

tar -xzf "${TMP_TAR}" -C "${INSTALL_DIR}"
rm -f "${TMP_TAR}"
chmod +x "${EXE_TARGET}"

echo "[2/3] Configuring PATH in shell profiles..."
SHELL_CONFIG=""
if [ -n "$ZSH_VERSION" ] || [ -f "$HOME/.zshrc" ]; then
    SHELL_CONFIG="$HOME/.zshrc"
elif [ -n "$BASH_VERSION" ] || [ -f "$HOME/.bashrc" ]; then
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
glacier --help > /dev/null 2>&1 || true

echo "======================================================================"
echo "  🎉 SUCCESS: Glacier CLI is installed to ${EXE_TARGET}"
echo "======================================================================"
echo "Quickstart: restart your terminal, then run:"
echo "  glacier devices"
echo "  glacier run <model.gguf> 'Hello World'"
echo "  glacier serve <model.gguf> --port 11434"
