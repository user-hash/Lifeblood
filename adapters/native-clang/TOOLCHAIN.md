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
