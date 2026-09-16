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
id exclusively to `../../artifacts/nuget`, so it always picks up the package
you just packed rather than whatever version (if any) is on nuget.org.
