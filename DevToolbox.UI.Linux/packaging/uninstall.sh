#!/bin/sh
# Removes what install.sh added. Your settings in ~/.local/share/DevToolbox/Config stay, as do
# your log search database and hosts backups — delete ~/.local/share/DevToolbox to remove those.
set -eu

data="${XDG_DATA_HOME:-$HOME/.local/share}"
root="$data/DevToolbox"

if [ -x "$root/bin/app/devtoolbox" ]; then "$root/bin/app/devtoolbox" --quit || true; fi

rm -rf "$root/bin/app"
rm -f "$HOME/.local/bin/devtoolbox" \
      "$data/applications/devtoolbox.desktop" \
      "$data/icons/hicolor/256x256/apps/devtoolbox.png" \
      "$HOME/.config/autostart/devtoolbox.desktop"

echo "DevToolbox is uninstalled. The MCP server is still in $root/bin/mcp; remove it with"
echo "  claude mcp remove --scope user devtoolbox && rm -rf $root/bin/mcp"
