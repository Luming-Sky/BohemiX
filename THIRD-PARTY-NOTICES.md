# Third-Party Notices

BohemiX is built from the following direct NuGet dependencies. Their license texts and notices must be preserved in any redistributed build according to each package's terms.

- Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, and Semi.Avalonia
- AnimatedImage.Avalonia and AnimatedImage.Native
- CommunityToolkit.Mvvm
- Dapper
- Facepunch.Steamworks
- LibVLCSharp and VideoLAN.LibVLC.Windows
- Microsoft.Extensions.DependencyInjection
- Microsoft.Web.WebView2
- NAudio
- Serilog and Serilog.Sinks.File
- SharpCompress and SharpGLTF.Core
- SkiaSharp
- Silk.NET.OpenGL
- SQLitePCLRaw and Microsoft.Data.Sqlite

The exact resolved dependency graph is recorded by `dotnet list BohemiX.sln package --include-transitive` during release review.

## usvfs

BohemiX includes an unmodified x64 binary from usvfs v0.5.7.2.

usvfs - User-Space Virtual File System, Copyright (C) Sebastian Herbord

- Repository: https://github.com/ModOrganizer2/usvfs
- Release: https://github.com/ModOrganizer2/usvfs/releases/tag/v0.5.7.2
- License: GNU GPL version 3 with the upstream section 7 additional permissions
- Binary SHA-256: `7EE7758433AB76713900E661056BE8074B9C567971FDE38FD0E514C76895E274`

The complete upstream license and dependency notices are distributed in the release package's `native` directory.
