# Devolutions.Terminal.Control sample

A minimal Avalonia desktop app that consumes the published
[`Devolutions.Terminal.Control`](../../src/Devolutions.Terminal.Control) NuGet
package via a plain `PackageReference` — never a `ProjectReference`. It exists
for two reasons:

1. **Living usage example** for anyone integrating the control into their own
   Avalonia app.
2. **Consumer smoke test** for CI: the `nuget-pack` workflow job packs
   `Devolutions.Terminal.Control` locally, then builds this project against
   that freshly produced package to catch packaging regressions (missing
   bundled assemblies/assets, broken dependencies, wrong TFM, etc.) that a
   `ProjectReference`-based build would never surface.

This project is intentionally **not** part of `Devolutions.Terminal.slnx` —
it must resolve `Devolutions.Terminal.Control` from a NuGet feed, so it can't
be part of the normal restore/build/test path that runs before the package
has ever been packed.

## Running it locally

```pwsh
# From the repository root:
dotnet pack src/Devolutions.Terminal.Control/Devolutions.Terminal.Control.csproj -c Release -o artifacts/nuget
dotnet run --project samples/Devolutions.Terminal.Control.Sample
```

`NuGet.Config` in this folder maps the `Devolutions.Terminal.Control` package
id exclusively to `../../artifacts/nuget`, so it never resolves a version from
nuget.org. Local packs all share the repository's `VersionPrefix`, and NuGet
reuses a version already in its global packages folder, so after repacking
either pass a new version (`-p:VersionPrefix=2026.3.1`) or clear the cached
copy (`dotnet nuget locals global-packages --clear`).

On Linux and macOS the sample launches `$SHELL` through the package's
`dt-pty-host`. A local pack only contains the hosts that exist under
`native/linux-pty/<rid>/`; see the NuGet section of
[docs/release.md](../../docs/release.md) for building them.

## Headless PTY smoke test

```pwsh
dotnet run --project samples/Devolutions.Terminal.Control.Sample -- --pty-smoke
```

`--pty-smoke` skips the UI, starts the package's own PTY transport (ConPTY on
Windows, `dt-pty-host` on Linux and macOS), and exits with 0 once the command
it ran produced output. CI runs it on Windows, Linux x64/ARM64, and macOS ARM64.
