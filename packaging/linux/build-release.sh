#!/bin/bash
# Builds the NexZen Desktop release tree for Linux x64 and packs it as a tarball.
#
#   packaging/linux/build-release.sh [--version 0.1.0] [--cef /path/to/cef_binary_152...] [--no-tar]
#
# Output (packaging/linux/out/):
#   nexzen-desktop-<version>-linux-x64/      the release tree (also used by AppImage, deb, rpm, AUR, Flatpak)
#   nexzen-desktop-<version>-linux-x64.tar.gz
#
# The tree carries its own .NET 8 runtime (dotnet/) and the CEF 152 runtime, so it only needs
# common system libraries (GTK/X11, NSS, ALSA/Pulse, FFmpeg for NexZen Mirror).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
version="${NEXZEN_VERSION:-0.1.0}"
cef_root="${NEXZEN_CEF152_ROOT:-$repo/third_party/cef152}"
make_tar=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --cef) cef_root="$2"; shift 2 ;;
    --no-tar) make_tar=0; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done
cef_root="$(cd "$cef_root" && pwd)"
[[ -f "$cef_root/Release/libcef.so" ]] || { echo "CEF 152 not found at $cef_root" >&2; exit 1; }

out="$here/out"
name="nexzen-desktop-$version-linux-x64"
tree="$out/$name"
rm -rf "$tree" "$out/$name.tar.gz"
mkdir -p "$tree"

echo "== dotnet publish ($version)"
dotnet publish "$repo/src/NexZen.Desktop/NexZen.Desktop.csproj" -c Release -r linux-x64 \
  --self-contained false -p:Cef152Root="$cef_root" -p:Version="$version" \
  -p:DebugType=none -p:GenerateDocumentationFile=false -o "$tree/app"

app="$tree/app"
built="$repo/src/NexZen.Desktop/bin/Release/net8.0/linux-x64"

# The project reference to the browser subprocess drops a self-contained copy of it, with
# its own .NET runtime files, into the app folder. The app is framework-dependent and only
# loads what NexZen.Desktop.deps.json lists, so those copies are dead weight.
echo "== removing runtime files the app does not use"
deps="$app/NexZen.Desktop.deps.json"
rm -f "$app"/Xilium.CefGlue.BrowserProcess* "$app"/*.xml "$app"/*.pdb
for f in "$app"/*; do
  [[ -f "$f" ]] || continue
  b="$(basename "$f")"
  case "$b" in
    NexZen.Desktop|NexZen.Desktop.*) continue ;;
  esac
  if [[ "$b" == *.dll || "$b" == *.so || "$b" == *.a || "$b" == createdump ]]; then
    grep -qF "\"$b\"" "$deps" || grep -qF "/$b\"" "$deps" || rm -f "$f"
  fi
done

echo "== CEF 152 runtime"
sub="$app/CefGlueBrowserProcess"
rm -rf "$sub"
mkdir -p "$sub"
# Subprocess host built against CEF 152 (UseCef152Linux copies it into the build output).
for f in Xilium.CefGlue.BrowserProcess Xilium.CefGlue.BrowserProcess.dll \
         Xilium.CefGlue.BrowserProcess.deps.json Xilium.CefGlue.BrowserProcess.runtimeconfig.json \
         Xilium.CefGlue.dll Xilium.CefGlue.Common.Shared.dll; do
  cp -L "$built/CefGlueBrowserProcess/$f" "$sub/$f"
done
cef_file() { # name -> real path inside the CEF distribution (Release/ links may be dangling)
  if [[ -e "$cef_root/Resources/$1" ]]; then echo "$cef_root/Resources/$1"
  elif [[ -e "$cef_root/Release/$1" ]]; then echo "$cef_root/Release/$1"
  fi
}
for f in icudtl.dat resources.pak chrome_100_percent.pak chrome_200_percent.pak v8_context_snapshot.bin \
         libvk_swiftshader.so libvulkan.so.1 vk_swiftshader_icd.json libEGL.so libGLESv2.so; do
  src="$(cef_file "$f")"
  [[ -n "$src" ]] && cp -L "$src" "$sub/$f"
done
echo "   stripping libcef.so"
strip --strip-unneeded -o "$sub/libcef.so" "$cef_root/Release/libcef.so"
cp -rL "$(cef_file locales)" "$sub/locales"
# CEF looks for the resource bundles beside the main executable as well.
rm -rf "$app/locales"
for f in resources.pak chrome_100_percent.pak chrome_200_percent.pak icudtl.dat; do
  ln -sfn "CefGlueBrowserProcess/$f" "$app/$f"
done
ln -sfn CefGlueBrowserProcess/locales "$app/locales"

echo "== .NET runtime"
dotnet_root="${NEXZEN_DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
rt="$(ls -d "$dotnet_root"/shared/Microsoft.NETCore.App/8.0.* | sort -V | tail -1)"
fxr="$(ls -d "$dotnet_root"/host/fxr/* | sort -V | tail -1)"
mkdir -p "$tree/dotnet/shared/Microsoft.NETCore.App" "$tree/dotnet/host/fxr"
cp -a "$rt" "$tree/dotnet/shared/Microsoft.NETCore.App/"
cp -a "$fxr" "$tree/dotnet/host/fxr/"
for f in LICENSE.txt ThirdPartyNotices.txt; do
  [[ -f "$dotnet_root/$f" ]] && cp "$dotnet_root/$f" "$tree/dotnet/"
done
echo "   $(basename "$rt") (hostfxr $(basename "$fxr"))"

echo "== launcher, desktop entry, icons"
install -m755 "$here/nexzen-desktop" "$tree/nexzen-desktop"
install -Dm644 "$here/nexzen-desktop.desktop" "$tree/share/applications/nexzen-desktop.desktop"
for s in 16 32 48 64 128 256 512; do
  d="$tree/share/icons/hicolor/${s}x${s}/apps"
  mkdir -p "$d"
  if command -v magick >/dev/null; then
    magick "$here/icons/nexzen-desktop.png" -resize ${s}x${s} "$d/nexzen-desktop.png"
  elif command -v convert >/dev/null; then
    convert "$here/icons/nexzen-desktop.png" -resize ${s}x${s} "$d/nexzen-desktop.png"
  elif [[ $s == 512 ]]; then
    cp "$here/icons/nexzen-desktop.png" "$d/nexzen-desktop.png"
  fi
done
install -Dm644 "$here/nexzen-desktop.metainfo.xml" "$tree/share/metainfo/nexzen-desktop.metainfo.xml"
sed -i "s/@VERSION@/$version/g" "$tree/share/metainfo/nexzen-desktop.metainfo.xml"
cp "$repo/LICENSE" "$repo/README.md" "$repo/THIRD-PARTY-NOTICES.md" "$tree/"
echo "$version" > "$tree/VERSION"

if [[ $make_tar == 1 ]]; then
  echo "== tarball"
  tar -C "$out" -czf "$out/$name.tar.gz" "$name"
  ls -la "$out/$name.tar.gz"
fi
du -sh "$tree"
