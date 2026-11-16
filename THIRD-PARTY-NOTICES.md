# Third-party notices

NexZen Desktop — Copyright © 2026 Infun — is licensed under the GNU General Public License v3.0 (see `LICENSE`).
It builds on the following third-party software, each under its own license:

| Component | Use in NexZen | License |
|---|---|---|
| [Chromium Embedded Framework (CEF)](https://bitbucket.org/chromiumembedded/cef) / Chromium | Web engine (not bundled in this repository; linked from a CEF 152 distribution) | BSD-3-Clause; Chromium's bundled third-party licenses are listed in the CEF distribution (`CREDITS.html`) |
| [Xilium CefGlue](https://github.com/OutSystems/CefGlue) (modified, `src/libs_cefglue`) | .NET bindings for CEF, Avalonia integration | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | UI framework | MIT |
| [Inter font](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | UI font | SIL Open Font License 1.1 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM helpers | MIT |
| [Entity Framework Core](https://github.com/dotnet/efcore) / Microsoft.Extensions.DependencyInjection | Local database, dependency injection | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) / [SQLite](https://sqlite.org) | Database engine | Apache-2.0 / public domain |
| [Konscious.Security.Cryptography.Argon2](https://github.com/kmaragon/Konscious.Security.Cryptography) | PIN / vault key derivation | MIT |
| [Makaretu.Dns.Multicast](https://github.com/richardschneider/net-mdns) | LAN discovery (mDNS) | MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | BlueZ (Bluetooth) access | MIT |
| [Net.Codecrete.QrCodeGenerator](https://github.com/manuelbl/QrCodeGenerator) | QR codes | MIT |
| [three.js](https://github.com/mrdoob/three.js) (incl. GLTFLoader, RoomEnvironment, BufferGeometryUtils) | 3D companion on internal pages | MIT |
| [FFmpeg](https://ffmpeg.org) — libavcodec, libavutil, libswscale | NexZen Mirror video/audio decoding (system libraries loaded at run time, not bundled) | LGPL-2.1-or-later (depending on the build) |
| PulseAudio `libpulse-simple` | NexZen Mirror sound output (system library, not bundled) | LGPL-2.1-or-later |
| [xUnit](https://github.com/xunit/xunit) | Tests only | Apache-2.0 |

The full license texts are available at the linked projects and in their NuGet packages.

## NexZen's own assets

The 3D companion models `src/NexZen.Desktop/Assets/NewTab/companion.glb` and `companion2.glb` were created by Infun
and are dedicated to the public domain under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/):
anyone may use, change and share them for any purpose, without asking.
