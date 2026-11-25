#!/usr/bin/env bash
# GK6X for Linux - full removal.
#   ./linux/uninstall.sh            remove the program, service, launcher and udev rule (keeps your settings)
#   ./linux/uninstall.sh --purge    also delete ~/.config/gk6x (your settings and key mappings) and logs
set -uo pipefail
PURGE=0; [ "${1:-}" = "--purge" ] && PURGE=1

systemctl --user disable --now gk6x.service 2>/dev/null
rm -f "$HOME/.config/systemd/user/gk6x.service"
systemctl --user daemon-reload 2>/dev/null
rm -rf "$HOME/.local/share/gk6x"
rm -f "$HOME/.local/bin/gk6x" \
      "$HOME/.local/share/applications/gk6x.desktop" \
      "$HOME/.local/share/icons/hicolor/scalable/apps/gk6x.svg"
command -v update-desktop-database >/dev/null && update-desktop-database "$HOME/.local/share/applications" 2>/dev/null

if [ -e /etc/udev/rules.d/70-gk6x.rules ]; then
  echo "Removing the udev rule (sudo)..."
  sudo -S -p '' rm -f /etc/udev/rules.d/70-gk6x.rules && sudo -S -p '' udevadm control --reload && \
    sudo udevadm trigger --subsystem-match=hidraw --action=change
fi
if [ "$PURGE" -eq 1 ]; then
  rm -rf "$HOME/.config/gk6x" "$HOME/.local/state/gk6x"
  echo "Settings and logs deleted."
else
  echo "Your settings are still in ~/.config/gk6x (use --purge to delete them)."
fi
echo "GK6X removed. The keyboard keeps whatever key mapping was last written to it; press its reset combo to restore defaults."
