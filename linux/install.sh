#!/usr/bin/env bash
# GK6X for Linux - installer (run it as your normal user, NOT with sudo).
#
#   ./linux/install.sh                 build + install for the current user
#   ./linux/install.sh --autostart     ...and start the lighting service at every login
#   ./linux/install.sh --no-udev       skip the (sudo) udev rule step
#
# What it does:
#   ~/.local/share/gk6x/app            the program (built with the .NET SDK)
#   ~/.local/bin/gk6x                  command
#   ~/.config/gk6x                     your settings / key mappings (never touched by updates)
#   ~/.config/systemd/user/gk6x.service
#   ~/.local/share/applications/gk6x.desktop (+ icon)
#   /etc/udev/rules.d/70-gk6x.rules    only step that needs sudo (access to the keyboard without root)
set -euo pipefail

AUTOSTART=0
UDEV=1
for arg in "$@"; do
  case "$arg" in
    --autostart) AUTOSTART=1 ;;
    --no-udev) UDEV=0 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg"; exit 1 ;;
  esac
done

if [ "$(id -u)" -eq 0 ]; then
  echo "Please run this as your normal user (it asks for sudo only for the udev rule)."; exit 1
fi

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$HOME/.local/share/gk6x/app"
BIN="$HOME/.local/bin"

command -v dotnet >/dev/null || {
  echo "The .NET SDK (8 or newer) is required to build GK6X."
  echo "  Arch/CachyOS: sudo pacman -S dotnet-sdk"
  echo "  Fedora:       sudo dnf install dotnet-sdk-8.0"
  echo "  Ubuntu:       sudo apt install dotnet-sdk-8.0"
  exit 1
}
dotnet --list-sdks | grep -E '^(8|9|1[0-9])\.' >/dev/null || { echo "Found dotnet, but no SDK 8 or newer."; exit 1; }
# libudev is used by HidSharp to find the keyboard
ldconfig -p 2>/dev/null | grep libudev.so.1 >/dev/null || echo "Warning: libudev.so.1 not found (install systemd-libs / libudev1)."

echo "==> Building"
rm -rf "$APP.new"
dotnet publish "$REPO/GK6X/GK6X.csproj" -c Release -o "$APP.new" --nologo -v quiet -p:DebugType=None
rm -rf "$APP.new/Data" "$APP.new/UserData"
cp -r "$REPO/Build/Data" "$APP.new/Data"
mkdir -p "$APP.new/UserData"
cp "$REPO/Build/UserData/"*Sample*.txt "$APP.new/UserData/" 2>/dev/null || true

echo "==> Installing to $APP"
# stop the running service so the files can be replaced
systemctl --user stop gk6x.service 2>/dev/null || true
rm -rf "$APP.old"; [ -d "$APP" ] && mv "$APP" "$APP.old"
mv "$APP.new" "$APP"; rm -rf "$APP.old"

mkdir -p "$BIN"
cat > "$BIN/gk6x" <<LAUNCH
#!/bin/sh
exec dotnet "$APP/GK6X.dll" "\$@"
LAUNCH
chmod +x "$BIN/gk6x"

echo "==> Service, launcher and icon"
mkdir -p "$HOME/.config/systemd/user" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/scalable/apps"
install -m644 "$REPO/linux/gk6x.service" "$HOME/.config/systemd/user/gk6x.service"
install -m644 "$REPO/linux/gk6x.desktop" "$HOME/.local/share/applications/gk6x.desktop"
# menu launchers don't always have ~/.local/bin in PATH
sed -i "s|^Exec=.*|Exec=$BIN/gk6x ui|" "$HOME/.local/share/applications/gk6x.desktop"
# Use the Papirus keyboard icon when the icon theme has it, otherwise the bundled gk6x.svg
if find /usr/share/icons "$HOME/.local/share/icons" -name 'system-keyboard-mx.svg' 2>/dev/null | grep . >/dev/null; then
  sed -i 's|^Icon=.*|Icon=system-keyboard-mx|' "$HOME/.local/share/applications/gk6x.desktop"
fi
install -m644 "$REPO/linux/gk6x.svg" "$HOME/.local/share/icons/hicolor/scalable/apps/gk6x.svg"
systemctl --user daemon-reload 2>/dev/null || true
command -v update-desktop-database >/dev/null && update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
if [ "$AUTOSTART" -eq 1 ]; then
  systemctl --user enable gk6x.service
  echo "    The lighting service will start at every login."
fi

if [ "$UDEV" -eq 1 ]; then
  echo "==> udev rule (needs sudo, this is the only step that does)"
  if sudo install -m644 "$REPO/linux/70-gk6x.rules" /etc/udev/rules.d/70-gk6x.rules; then
    sudo -S -p '' udevadm control --reload
    sudo -S -p '' udevadm trigger --subsystem-match=hidraw --action=change
    echo "    Done. If the keyboard is not detected yet, unplug and replug it once."
  else
    echo "    Skipped. Without the rule you must run gk6x with sudo."
  fi
fi

case ":$PATH:" in *":$BIN:"*) ;; *) echo; echo "NOTE: add $BIN to your PATH (it is not in it right now)." ;; esac
cat <<DONE

Installed. Try:
  gk6x list                 # shows the keyboard interfaces
  gk6x daemon               # start the realtime lighting service (or: systemctl --user start gk6x)
  gk6x ui                   # open the control page   (http://127.0.0.1:6465)
  gk6x help
DONE
