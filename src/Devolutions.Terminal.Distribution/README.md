# Devolutions Terminal NuGet distribution

`Devolutions.Terminal.App` contains self-contained NativeAOT distributions for
Windows (`win-x64`, `win-arm64`), Linux (`linux-x64`, `linux-arm64`), and macOS
(`osx-x64`, `osx-arm64`). The package is a distribution artifact rather than a
managed API package.

Reference the package from an SDK-style project:

```xml
<PackageReference Include="Devolutions.Terminal.App" Version="2026.3.0" />
```

The main package depends on one payload package per supported RID so that each
published package remains below NuGet.org's package-size limit. Build-transitive
MSBuild targets copy only the payload matching the consuming project's
`RuntimeIdentifier` into its output. Projects without a `RuntimeIdentifier`
continue to receive the `win-x64` payload by default.

The command-line executable is `dt.exe` on Windows and `dt` on Linux and macOS;
no `wt.exe` alias or Windows Terminal compatibility shim is installed.
