#!/bin/sh
# Copies a brain UF2 to a Pico in BOOTSEL mode (plan 0006 N5). Usage: ./flash.sh FILE.uf2
# Hold BOOTSEL while plugging the Pico in: it shows up as a drive (RPI-RP2, or RP2350 on a Pico 2).
# METRODOM_UF2_DIR names the drive yourself (an unusual mount point, or a test).
# METRODOM_FLASH_WAIT is how many seconds to wait for it (default 60).
set -eu
uf2=${1:?usage: flash.sh FILE.uf2}
[ -f "$uf2" ] || { echo "flash.sh: no $uf2 (run make build first)"; exit 1; }
wait=${METRODOM_FLASH_WAIT:-60}

find_drive() {
  if [ -n "${METRODOM_UF2_DIR:-}" ]; then
    [ -d "$METRODOM_UF2_DIR" ] && echo "$METRODOM_UF2_DIR"
    return 0
  fi
  for d in /Volumes/RPI-RP2 /Volumes/RP2350 /media/*/RPI-RP2 /media/*/RP2350 /run/media/*/RPI-RP2 /run/media/*/RP2350; do
    [ -d "$d" ] && { echo "$d"; return 0; }
  done
  return 0
}

drive=$(find_drive)
if [ -z "$drive" ]; then
  echo "Hold BOOTSEL on the Pico and plug it in (waiting up to ${wait} s)..."
  i=0
  while [ -z "$drive" ] && [ "$i" -lt "$wait" ]; do
    sleep 1
    i=$((i + 1))
    drive=$(find_drive)
  done
fi
[ -n "$drive" ] || { echo "flash.sh: no Pico in BOOTSEL mode found (looked for an RPI-RP2 or RP2350 drive)"; exit 1; }

cp "$uf2" "$drive/"
sync
echo "Flashed $(basename "$uf2") to $drive. The brain restarts on its own."
echo "Check: metrodom devices --identify"
