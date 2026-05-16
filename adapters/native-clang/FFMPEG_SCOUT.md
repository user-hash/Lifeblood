# FFmpeg Scout Workflow

This is the repeatable reconnaissance path for checking FFmpeg with the native
Clang adapter. It is intentionally a scout workflow, not a full FFmpeg support
claim.

## What This Proves

The scout verifies that Lifeblood can:

- configure a minimal FFmpeg build profile with LLVM clang;
- generate a focused `compile_commands.json`;
- parse representative FFmpeg translation units through `libclang`;
- emit a graph that Lifeblood can import, validate, and analyze;
- surface large native tables as row/cell facts.

The default file list targets registration-heavy and table-heavy files:

- `libavfilter/allfilters.c`
- `libavcodec/allcodecs.c`
- `libavformat/allformats.c`
- `libavutil/pixdesc.c`
- `libswscale/utils.c`

## Prerequisites

On Windows, the current scout path expects:

- Git;
- Git Bash;
- LLVM clang;
- Visual Studio Build Tools;
- a built `artifacts/native-clang-build/lifeblood-native-clang.exe`.

The helper discovers the clang resource include directory and Visual Studio
system include paths, then writes them into the compile database. That is
required because a hand-written FFmpeg compile command without system includes
fails immediately on headers like `stdint.h`, `stddef.h`, and `stdio.h`.

## Run

From the Lifeblood repo root:

```powershell
powershell -ExecutionPolicy Bypass `
  -File adapters/native-clang/tools/ffmpeg-scout/Prepare-FfmpegScout.ps1
```

Defaults:

- checkout root: `D:\Projekti\ffmpeg-lifeblood-scout\ffmpeg`
- build root: `D:\Projekti\ffmpeg-lifeblood-scout\ffmpeg\build-lifeblood-clang`
- graph output: `D:\Projekti\ffmpeg-lifeblood-scout\ffmpeg-scout.graph.json`
- profile: `ffmpeg-clang-minimal-scout`

Useful rerun when the checkout/configure step already exists:

```powershell
powershell -ExecutionPolicy Bypass `
  -File adapters/native-clang/tools/ffmpeg-scout/Prepare-FfmpegScout.ps1 `
  -SkipClone `
  -SkipConfigure
```

To prepare only the compile database without running extraction:

```powershell
powershell -ExecutionPolicy Bypass `
  -File adapters/native-clang/tools/ffmpeg-scout/Prepare-FfmpegScout.ps1 `
  -SkipRun
```

To try a different slice, copy `tools/ffmpeg-scout/default-files.txt`, edit the
file list, and pass `-FilesList <path>`.

## First Local Result

On 2026-05-16, the default scout produced:

- `9264` native graph symbols;
- `5190` raw adapter edges;
- `14494` Lifeblood-imported edges after graph synthesis;
- `1067` methods;
- `87` files;
- `0` architecture violations;
- `1` likely real cycle in `libswscale` context initialization;
- hundreds of table row/cell facts, especially from `libavutil/pixdesc.c`.

## Current Limits

This workflow does not yet replace a real whole-build compile database. The
machine used for the first scout did not have WSL, Make, Bear, or compiledb
available, so the helper creates a focused compile database for selected files.

The next maturity step is to support one of these full-build paths:

- WSL + `bear -- ./configure && make`;
- MSYS2/MinGW + Make/Bear;
- a project-specific FFmpeg compile database generator that reads `config.mak`
  and library make fragments.

Until then, scout results are valuable for feature discovery and performance
smoke tests, not whole-repo coverage metrics.
