# Native Clang Toolchain

**Last verified:** 2026-05-16 on Windows.

This file records the local native toolchain state for the Lifeblood native
adapter track. It is intentionally factual: what exists on this machine, what
does not, and what that means for the next implementation chunk.

## Installed Tools

LLVM was installed with:

```powershell
winget install --id LLVM.LLVM -e --accept-package-agreements --accept-source-agreements --disable-interactivity
```

Chocolatey was attempted first but could not write to `C:\ProgramData` from a
non-elevated shell. Winget succeeded.

Verified binaries:

| Tool | Path | Version / note |
| --- | --- | --- |
| `clang.exe` | `C:\Program Files\LLVM\bin\clang.exe` | 22.1.5 |
| `clang++.exe` | `C:\Program Files\LLVM\bin\clang++.exe` | 22.1.5 |
| `clang-cl.exe` | `C:\Program Files\LLVM\bin\clang-cl.exe` | 22.1.5 |
| `cl.exe` | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\cl.exe` | MSVC 19.44.35221 |
| `cmake.exe` | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe` | 3.31.6-msvc6 |
| `ninja.exe` | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe` | 1.12.1 |

The current shell may not see LLVM on `PATH` until a new terminal/session is
opened. Use absolute paths in scripts until the adapter has an explicit
toolchain discovery helper.

## Verified Clang Parse

The tiny fixture parses successfully:

```powershell
& 'C:\Program Files\LLVM\bin\clang.exe' `
  -I adapters/native-clang/test-fixtures/tiny-c/src `
  -fsyntax-only `
  adapters/native-clang/test-fixtures/tiny-c/src/decode.c
```

Exit code: `0`.

## Verified Native Adapter Build

The Stage 1 executable builds when CMake is launched from the Visual Studio
developer environment. A plain shell can find `clang++.exe` but may not have the
MSVC and Windows SDK library paths needed for linking.

Verified build shape:

```powershell
cmd /c 'call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 && "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -S adapters/native-clang -B artifacts/native-clang-build -G Ninja -DCMAKE_MAKE_PROGRAM="C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" -DCMAKE_CXX_COMPILER="C:/Program Files/LLVM/bin/clang++.exe" -DCMAKE_RC_COMPILER="C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe" && "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build artifacts/native-clang-build'
```

Verified tiny run shape:

```powershell
artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project adapters/native-clang/test-fixtures/tiny-c `
  --profile tiny-debug `
  --out artifacts/native-clang-build/tiny.graph.json

dotnet run --project src/Lifeblood.CLI -- analyze --graph artifacts/native-clang-build/tiny.graph.json
```

The tiny graph import reports 8 symbols, 11 edges, 1 module, and 1 type. The
direct-reference graph import reports 14 symbols, 22 edges, 1 module, and
3 types. The profile fixture currently imports as 12 symbols / 19 edges for
`video`, and 12 symbols / 17 edges for `audio`. The edge counts include
graph-builder synthesis such as containment and derived file edges.

## Available Clang API Surface

The official Windows LLVM installer includes the C API:

| File | Present |
| --- | --- |
| `C:\Program Files\LLVM\include\clang-c\Index.h` | yes |
| `C:\Program Files\LLVM\include\clang-c\CXCompilationDatabase.h` | yes |
| `C:\Program Files\LLVM\lib\libclang.lib` | yes |
| `C:\Program Files\LLVM\bin\libclang.dll` | yes |

It does **not** include the C++ LibTooling development surface:

| File / pattern | Present |
| --- | --- |
| `C:\Program Files\LLVM\include\clang\Tooling\CommonOptionsParser.h` | no |
| `C:\Program Files\LLVM\lib\*Tooling*.lib` | no |
| `C:\Program Files\LLVM\bin\llvm-config.exe` | no |

## Implementation Consequence

Stage 1 should bootstrap with a small native executable that uses `libclang`
and `clang-c/CXCompilationDatabase.h`. That keeps the local setup small and
still gives compiler-backed parsing through the real `compile_commands.json`
contract.

C++ LibTooling remains a valid richer option, but it requires a separate
LLVM development package/source-build story. Do not block the tiny fixture
extractor on that heavier toolchain until `libclang` proves insufficient.
