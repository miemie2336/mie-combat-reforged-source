# MIE Combat Reforged v2.0.6 host source

This source builds the Windows x64 host used by MIE Combat Reforged - Boss AI Overhaul for Phantom Blade Zero, Steam build 26367565. The host displays the F6 settings panel, handles the F8 toggle, and plays the perilous attack cue using WASAPI. It exchanges settings and event files with the UE4SS Lua module.

## Build

1. Use 64-bit Windows with .NET Framework 4.8 and Windows PowerShell.
2. Open PowerShell in this directory and run `.\Build.ps1`.
3. The script invokes the 64-bit .NET Framework C# compiler with `PBZSuiteHost` as the entry point and the `WASAPI` symbol enabled. It writes `build\PBZSuiteHost.exe`, the executable installed under `platform\steam\ue4ss\Mods\PBZSuite\`.

## Nexus release reference

- Package: Combat-Reforged-Boss-AI-On-With-Item-Balance-26367565-README-Fixed.zip
- Package SHA-256: C840D177FEE4C6CFC6811567FBB5D3888A12A59093C26D4021EC01875C73C1A5
- Packaged PBZSuiteHost.exe SHA-256: 74EEE3282309B0890F356A637D1A06B3ABE6EA571F11DE9E497650559ACB08B9

The rebuilt executable has the same IL bytes for all 114 managed method bodies as the packaged executable. Build metadata such as the module identifier may differ, so the whole-file hash of a rebuild can differ.
