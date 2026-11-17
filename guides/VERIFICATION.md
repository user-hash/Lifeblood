# Verification and known limits

Changes since the latest public release are listed in [CHANGELOG](../CHANGELOG.md).
Source tests, packaged-tool checks and installed-client acceptance are separate
boundaries. A passing source run does not certify a later package or deployment.

## Reproduce

Use the SDK selected by `global.json` and the prerequisites in [setup](MCP_SETUP.md)
and [native-Clang guidance](NATIVE_CLANG.md):

```sh
dotnet build Lifeblood.sln -c Release
dotnet test Lifeblood.sln -c Release --no-build
dotnet run --project src/Lifeblood.CLI -c Release -- verify --incremental --project .
dotnet pack src/Lifeblood.CLI -c Release -o ./artifacts
dotnet pack src/Lifeblood.Server.Mcp -c Release --no-build -o ./artifacts
```

The native-Clang cases require the adapter and its toolchain. A skipped case
is not a passing qualification of that adapter. Follow [the release checklist](RELEASE.md)
for exact-commit CI and final package acceptance.

## Limits

- Host .NET, Roslyn and C# support leaves consumer compiler settings independent.
  Host emit does not validate a Unity, Player or Burst build.
- File guards certify captured bytes, not dependency freshness or a joint
  multi-file compilation. Refresh after coordinated API changes.
- Method effects are bounded static evidence with explicit indirect, deferred
  and native unknowns. Caller-supplied native checksums do not authenticate artifacts.
- Ordinary MCP execution runs in process. The optional isolated executor needs
  a caller-provided ScriptHost and is not a security sandbox.
- Extended shared-service memory stability and several integration checks remain
  open; source-test success does not establish extended deployment qualification.

Raw captures, private-workspace measurements, RPC transcripts, local package
manifests and session journals are retained outside the repository.
