#!/bin/sh
# Installs DevToolbox for this user: no root, nothing outside your home folder.
#
#   ./install.sh               build and install, add DevToolbox to the app menu
#   ./install.sh --autostart   the same, and start it (without a window) when you log in
#
# Where things go:
#   ~/.local/share/DevToolbox/bin/app   the app              (replaced on every install)
#   ~/.local/share/DevToolbox/bin/mcp   the MCP server for Claude Code
#   ~/.local/share/DevToolbox/Config    your settings        (never touched by this script)
#   ~/.local/bin/devtoolbox             the command
#   ~/.local/share/applications/devtoolbox.desktop, and the icon beside it
#
# Needs the .NET 10 SDK (sudo apt install dotnet-sdk-10.0). zenity (folder dialogs),
# notify-send (Service Pulse alerts) and Chrome or Chromium (app window) are used when present.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
data="${XDG_DATA_HOME:-$HOME/.local/share}"
root="$data/DevToolbox"
bin="$HOME/.local/bin"

command -v dotnet >/dev/null || { echo "dotnet was not found. Install it first: sudo apt install dotnet-sdk-10.0" >&2; exit 1; }

# A running copy holds the files being replaced.
if [ -x "$root/bin/app/devtoolbox" ]; then "$root/bin/app/devtoolbox" --quit || true; fi

echo "Building DevToolbox..."
dotnet publish "$repo/DevToolbox.UI.Linux" -c Release -o "$root/bin/app.new" --nologo -v quiet
rm -rf "$root/bin/app" && mv "$root/bin/app.new" "$root/bin/app"

echo "Building the MCP server..."
dotnet publish "$repo/DevToolbox.Mcp" -c Release -o "$root/bin/mcp.new" --nologo -v quiet
rm -rf "$root/bin/mcp" && mv "$root/bin/mcp.new" "$root/bin/mcp"

mkdir -p "$bin" "$data/applications" "$data/icons/hicolor/256x256/apps"
ln -sf "$root/bin/app/devtoolbox" "$bin/devtoolbox"
cp "$here/devtoolbox.png" "$data/icons/hicolor/256x256/apps/devtoolbox.png"

desktop_entry() {
    cat <<ENTRY
[Desktop Entry]
Type=Application
Name=DevToolbox
Comment=Projects, logs, Service Pulse and hosts switching
Exec="$root/bin/app/devtoolbox" $1
Icon=devtoolbox
Terminal=false
Categories=Development;
Keywords=projects;logs;hosts;powershell;
Actions=Quit;

[Desktop Action Quit]
Name=Quit DevToolbox
Exec="$root/bin/app/devtoolbox" --quit
ENTRY
}

desktop_entry "" > "$data/applications/devtoolbox.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" 2>/dev/null || true

if [ "${1:-}" = "--autostart" ]; then
    mkdir -p "$HOME/.config/autostart"
    desktop_entry "--no-window" > "$HOME/.config/autostart/devtoolbox.desktop"
    echo "DevToolbox will start when you log in (no window; open it from the app menu)."
fi

echo
echo "Installed. Open DevToolbox from the app menu, or run: devtoolbox"
echo "MCP server for Claude Code: $root/bin/mcp/DevToolbox.Mcp"
echo "  register it once with:  claude mcp add --scope user devtoolbox -- $root/bin/mcp/DevToolbox.Mcp"
