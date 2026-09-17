# Third-party notices

Clipboard Plus is distributed under Apache License 2.0. The self-contained Windows build includes third-party runtime components with their own licenses:

- .NET runtime, Windows Desktop/WPF and Windows Forms: MIT, https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core: MIT, https://github.com/dotnet/efcore/blob/main/LICENSE.txt
- SQLitePCLRaw components: Apache License 2.0, https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT
- SQLite: public domain, https://sqlite.org/copyright.html
- Inno Setup installer engine: Inno Setup License; the installed compiler's license text is included in `licenses/INNO-SETUP-LICENSE.txt`. Inno Setup is a build-time tool, not a running app dependency.

ffmpeg is used only by optional developer tests to generate synthetic fixtures. It is not included in or required by the application release. Windows media codecs and OCR are operating-system components.

Dependency license texts accompany the release under `licenses/`.
