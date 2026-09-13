#!/usr/bin/env bash
# Regenerate the committed Store PNGs from the existing 1024px app artwork on macOS.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p packaging/windows/Assets
for spec in Square44x44Logo:44 Square150x150Logo:150 StoreLogo:50; do
  name="${spec%:*}"
  size="${spec#*:}"
  sips -s format png --resampleHeightWidth "$size" "$size" \
    src/UrDatabase.App/Assets/UrDatabase.icns \
    --out "packaging/windows/Assets/$name.png" >/dev/null
  sips -s format png --resampleHeightWidth "$((size * 2))" "$((size * 2))" \
    src/UrDatabase.App/Assets/UrDatabase.icns \
    --out "packaging/windows/Assets/$name.scale-200.png" >/dev/null
  sips -s format png --resampleHeightWidth "$((size * 4))" "$((size * 4))" \
    src/UrDatabase.App/Assets/UrDatabase.icns \
    --out "packaging/windows/Assets/$name.scale-400.png" >/dev/null
done
for spec in 125:63 150:75; do
  scale="${spec%:*}"
  size="${spec#*:}"
  sips -s format png --resampleHeightWidth "$size" "$size" \
    src/UrDatabase.App/Assets/UrDatabase.icns \
    --out "packaging/windows/Assets/StoreLogo.scale-$scale.png" >/dev/null
done
cp packaging/windows/Assets/Square44x44Logo.png \
  packaging/windows/Assets/Square44x44Logo.targetsize-44_altform-unplated.png
