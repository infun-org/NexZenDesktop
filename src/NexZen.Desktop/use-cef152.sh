#!/bin/bash
# Replace the Chromium 120 redistributable next to the browser subprocess
# with the local CEF 152 build. Symlinks avoid copying the 1.4 GB library.
set -euo pipefail
root="${1:?cef root}"
dest="${2:?browser process dir}"
mkdir -p "$dest"
rm -rf "$dest/locales" "$dest/snapshot_blob.bin"
link() {
  ln -sfn "$1" "$dest/$(basename "$1")"
}
for name in libcef.so icudtl.dat resources.pak chrome_100_percent.pak \
            chrome_200_percent.pak v8_context_snapshot.bin; do
  if [[ -e "$root/Release/$name" ]]; then
    link "$root/Release/$name"
  elif [[ -e "$root/Resources/$name" ]]; then
    link "$root/Resources/$name"
  else
    echo "missing $name" >&2
    exit 1
  fi
done

# ANGLE/SwiftShader libraries are optional in newer CEF Linux distributions.
# Remove package leftovers when the selected CEF build does not ship them so
# an older NuGet runtime cannot be mixed with the selected libcef.so.
for name in libEGL.so libGLESv2.so libvk_swiftshader.so libvulkan.so.1 \
            vk_swiftshader_icd.json; do
  if [[ -e "$root/Release/$name" ]]; then
    link "$root/Release/$name"
  elif [[ -e "$root/Resources/$name" ]]; then
    link "$root/Resources/$name"
  else
    rm -f "$dest/$name"
  fi
done
# Copy locales into the build output. `dotnet clean` tracks the NuGet locale
# files and must be able to delete these output files without following a
# symlink into the read-only CEF distribution.
cp -a "$root/Resources/locales" "$dest/locales"
# Keep the canonical CEF resources layout available for the browser process.
ln -sfn "$root/Resources" "$dest/Resources"
# The subprocess host keeps its own copy of CefGlue. Refresh it from the
# just-built app output so it calls cef_api_hash with the CEF 152 version.
app="$(dirname "$dest")"
for name in resources.pak chrome_100_percent.pak chrome_200_percent.pak; do
  ln -sfn "$root/Resources/$name" "$app/$name"
done
rm -rf "$app/locales"
ln -sfn "$root/Resources/locales" "$app/locales"
for name in Xilium.CefGlue Xilium.CefGlue.Common.Shared; do
  cp -f "$app/$name.dll" "$dest/$name.dll"
  # No .pdb when building with DebugType=none (release packaging).
  if [[ -f "$app/$name.pdb" ]]; then
    cp -f "$app/$name.pdb" "$dest/$name.pdb"
  else
    rm -f "$dest/$name.pdb"
  fi
done
echo "CEF 152 linked into $dest"
