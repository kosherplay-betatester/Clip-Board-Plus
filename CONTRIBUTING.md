# Contributing

Use Windows 10 2004+ or Windows 11 with the .NET 10 SDK. Restore dependencies through the checked-in NuGet.Config.

```powershell
dotnet build ClipboardPlus/ClipboardPlus.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1
```

Before changes involving capture or playback, run `scripts/Test.ps1 -Clipboard -Media`. This briefly changes the real clipboard and restores it afterward. Media fixtures require ffmpeg on PATH; media uses Windows decoders. `-InputTest` requires clicking its Start button within two minutes. `-Benchmark` creates 10,000 synthetic entries. Tests use separate temporary profiles.

Use `--demo` for sample content without touching normal app history. Capture starts paused. `--demo --render-proof artifacts/visual` produces isolated dark/light, image and settings renders.

Keep clipboard payloads out of logs and repository files. Do not add polling loops, preload media, or load full images into history rows. Test both fresh and existing histories when changing stored summaries. Report expected versus actual behavior and the verification performed in each pull request.

To publish locally, run `scripts/Publish.ps1`. Generated binaries, fixtures, test histories and artifacts are excluded from Git. Release assets belong on GitHub Releases.
