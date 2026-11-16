#!/bin/bash
# Turns the release tree from build-release.sh into distribution packages.
#
#   packaging/linux/make-packages.sh [--version 0.1.0] deb rpm appimage
#
# deb      needs dpkg-deb (falls back to ar + tar)
# rpm      needs rpmbuild
# appimage needs appimagetool (APPIMAGETOOL=/path/to/appimagetool-x86_64.AppImage)
#
# deb and rpm install the tree to /opt/nexzen-desktop with /usr/bin/nexzen-desktop.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
version="${NEXZEN_VERSION:-0.1.0}"
formats=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    deb|rpm|appimage) formats+=("$1"); shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ ${#formats[@]} -gt 0 ]] || formats=(deb rpm appimage)

out="$here/out"
tree="$out/nexzen-desktop-$version-linux-x64"
[[ -x "$tree/nexzen-desktop" ]] || { echo "run build-release.sh --version $version first" >&2; exit 1; }
summary="Browse the web and mirror your Android phone side by side"
homepage="https://github.com/infun-org/NexZenDesktop"
maintainer="Infun <https://github.com/infun-org>"

# /opt/nexzen-desktop + /usr links, shared by deb and rpm.
stage_fhs() {
  local root="$1"
  rm -rf "$root"
  mkdir -p "$root/opt/nexzen-desktop" "$root/usr/bin" "$root/usr/share/doc/nexzen-desktop"
  cp -a "$tree/app" "$tree/dotnet" "$tree/nexzen-desktop" "$tree/VERSION" "$root/opt/nexzen-desktop/"
  cp -a "$tree/share/." "$root/usr/share/"
  cp "$tree/README.md" "$tree/THIRD-PARTY-NOTICES.md" "$tree/LICENSE" "$root/usr/share/doc/nexzen-desktop/"
  ln -s /opt/nexzen-desktop/nexzen-desktop "$root/usr/bin/nexzen-desktop"
}

make_deb() {
  local root="$out/deb-root" deb="$out/nexzen-desktop_${version}_amd64.deb"
  echo "== deb"
  stage_fhs "$root"
  mkdir -p "$root/DEBIAN"
  local size
  size="$(du -sk --exclude=DEBIAN "$root" | cut -f1)"
  cat > "$root/DEBIAN/control" <<EOF
Package: nexzen-desktop
Version: $version
Architecture: amd64
Maintainer: $maintainer
Installed-Size: $size
Section: web
Priority: optional
Homepage: $homepage
Depends: libnss3, libnspr4, libatk1.0-0t64 | libatk1.0-0, libatk-bridge2.0-0t64 | libatk-bridge2.0-0,
 libatspi2.0-0t64 | libatspi2.0-0, libcups2t64 | libcups2, libdbus-1-3, libglib2.0-0t64 | libglib2.0-0,
 libx11-6, libxcomposite1, libxdamage1, libxext6, libxfixes3, libxrandr2, libxcb1, libxkbcommon0,
 libgbm1, libexpat1, libcairo2, libpango-1.0-0, libudev1, libasound2t64 | libasound2,
 libfontconfig1, libssl3t64 | libssl3,
 libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70
Recommends: ffmpeg, libpulse0, xdg-utils
Suggests: adb
Description: $summary
 NexZen is a Chromium-based (CEF 152) desktop browser that works with NexZen for
 Android: mirror and control your phone next to the page you are reading (with
 sound, over USB or Wi-Fi, end-to-end encrypted), and send links, text and files
 between phone and computer. SuperMenu puts every browser function one
 right-click away.
EOF
  cat > "$root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
EOF
  cp "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"
  chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"
  rm -f "$deb"
  if command -v dpkg-deb >/dev/null; then
    dpkg-deb --root-owner-group -Zxz --build "$root" "$deb"
  else
    # Same layout dpkg-deb writes: debian-binary, control.tar.xz, data.tar.xz.
    local tmp="$out/deb-ar"
    rm -rf "$tmp"; mkdir -p "$tmp"
    echo "2.0" > "$tmp/debian-binary"
    tar --owner=0 --group=0 -C "$root/DEBIAN" -cJf "$tmp/control.tar.xz" .
    tar --owner=0 --group=0 --exclude=./DEBIAN -C "$root" -cJf "$tmp/data.tar.xz" .
    (cd "$tmp" && ar rc "$deb" debian-binary control.tar.xz data.tar.xz)
    rm -rf "$tmp"
  fi
  rm -rf "$root"
  ls -la "$deb"
}

make_rpm() {
  local top="$out/rpmbuild"
  echo "== rpm"
  rm -rf "$top"
  mkdir -p "$top"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
  stage_fhs "$top/SOURCES/root"
  cat > "$top/SPECS/nexzen-desktop.spec" <<EOF
%global debug_package %{nil}
%global __os_install_post %{nil}
%global __requires_exclude_from ^/opt/nexzen-desktop/.*\$
%global __provides_exclude_from ^/opt/nexzen-desktop/.*\$
Name:           nexzen-desktop
Version:        $version
Release:        1
Summary:        $summary
License:        GPL-3.0-or-later
URL:            $homepage
ExclusiveArch:  x86_64
AutoReqProv:    no
# Distribution-neutral library requirements (CEF 152 and .NET 8).
Requires:       libnss3.so()(64bit), libnspr4.so()(64bit), libatk-1.0.so.0()(64bit), libatk-bridge-2.0.so.0()(64bit)
Requires:       libatspi.so.0()(64bit), libcups.so.2()(64bit), libdbus-1.so.3()(64bit), libglib-2.0.so.0()(64bit)
Requires:       libX11.so.6()(64bit), libXcomposite.so.1()(64bit), libXdamage.so.1()(64bit), libXext.so.6()(64bit)
Requires:       libXfixes.so.3()(64bit), libXrandr.so.2()(64bit), libxcb.so.1()(64bit), libxkbcommon.so.0()(64bit)
Requires:       libgbm.so.1()(64bit), libexpat.so.1()(64bit), libcairo.so.2()(64bit), libpango-1.0.so.0()(64bit)
Requires:       libudev.so.1()(64bit), libasound.so.2()(64bit), libfontconfig.so.1()(64bit), libssl.so.3()(64bit)
Requires:       libicu
Recommends:     ffmpeg-libs, pulseaudio-libs

%description
NexZen is a Chromium-based (CEF 152) desktop browser that works with NexZen for
Android: mirror and control your phone next to the page you are reading (with
sound, over USB or Wi-Fi, end-to-end encrypted), and send links, text and files
between phone and computer. SuperMenu puts every browser function one
right-click away.

%install
cp -a %{_sourcedir}/root/. %{buildroot}/

%files
/opt/nexzen-desktop
/usr/bin/nexzen-desktop
/usr/share/applications/nexzen-desktop.desktop
/usr/share/icons/hicolor/*/apps/nexzen-desktop.png
/usr/share/metainfo/nexzen-desktop.metainfo.xml
%doc /usr/share/doc/nexzen-desktop
EOF
  rpmbuild --define "_topdir $top" --define "_build_id_links none" -bb "$top/SPECS/nexzen-desktop.spec"
  cp "$top"/RPMS/x86_64/nexzen-desktop-"$version"-1.x86_64.rpm "$out/"
  rm -rf "$top"
  ls -la "$out/nexzen-desktop-$version-1.x86_64.rpm"
}

make_appimage() {
  local appdir="$out/NexZen.AppDir" image="$out/NexZen-$version-x86_64.AppImage"
  local tool="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
  [[ -n "$tool" ]] || { echo "appimagetool not found (set APPIMAGETOOL)" >&2; return 1; }
  echo "== AppImage"
  rm -rf "$appdir"
  mkdir -p "$appdir/usr"
  cp -a "$tree/app" "$tree/dotnet" "$tree/nexzen-desktop" "$tree/VERSION" "$appdir/"
  cp -a "$tree/share" "$appdir/usr/"
  ln -s nexzen-desktop "$appdir/AppRun"
  cp "$tree/share/applications/nexzen-desktop.desktop" "$appdir/nexzen-desktop.desktop"
  cp "$tree/share/icons/hicolor/256x256/apps/nexzen-desktop.png" "$appdir/nexzen-desktop.png"
  ln -s nexzen-desktop.png "$appdir/.DirIcon"
  rm -f "$image"
  ARCH=x86_64 "$tool" --no-appstream "$appdir" "$image"
  rm -rf "$appdir"
  ls -la "$image"
}

for f in "${formats[@]}"; do
  "make_$f"
done
