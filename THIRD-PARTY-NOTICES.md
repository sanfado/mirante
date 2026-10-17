# Third-party notices

The Mirante source code is licensed under the MIT License (see `LICENSE`).
The Mirante installer also distributes the components below, each under its own license.

| Component | Version | License | Source |
|---|---|---|---|
| LibVLC (libvlc, libvlccore and a selected set of VLC plugins), by VideoLAN | 3.0.24 | LGPL-2.1-or-later | https://code.videolan.org/videolan/vlc · https://download.videolan.org/pub/videolan/vlc/ |
| FFmpeg (libavcodec, libswscale), built into the VLC plugins above | as built by VideoLAN for LibVLC 3.0.24 | LGPL-2.1-or-later | https://ffmpeg.org · VideoLAN contribs for the same VLC version |
| LIVE555 Streaming Media, built into VLC's RTSP plugin | as built by VideoLAN | LGPL | http://www.live555.com/liveMedia/ |
| LibVLCSharp, LibVLCSharp.WPF, by VideoLAN | 3.10.2 | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| WPF-UI, WPF-UI.Tray, by lepo.co | 4.3.0 | MIT | https://github.com/lepoco/wpfui |
| Fluent System Icons (font bundled with WPF-UI), by Microsoft | — | MIT | https://github.com/microsoft/fluentui-system-icons |
| CommunityToolkit.Mvvm, by Microsoft / .NET Foundation | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Velopack, by Velopack Ltd | 1.2.161 | MIT | https://github.com/velopack/velopack |

Not bundled: the .NET Desktop Runtime (MIT), which the installer downloads from Microsoft when missing.

## LGPL components

LibVLC and its plugins are shipped unmodified, as separate dynamic libraries in the
`libvlc\win-x64` folder of the installation, and can be replaced by compatible builds.
Only the plugins Mirante needs are included (RTSP, video decoding, video output, image
encoding for snapshots); the list is in `src/Mirante.App/Mirante.App.csproj`.
The complete corresponding source code is available from VideoLAN at the links above.

The full text of each license is available at https://opensource.org/license/mit and
https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html.
