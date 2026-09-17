# Devolutions Terminal NuGet distribution

`Devolutions.Terminal.App` is a .NET 10 RID-specific tool package containing
self-contained NativeAOT distributions for Windows (`win-x64`, `win-arm64`),
Linux (`linux-x64`, `linux-arm64`), and macOS (`osx-x64`, `osx-arm64`). The
package is a distribution artifact rather than a managed API package.

Install the tool using the .NET 10 SDK or newer:

```powershell
dotnet tool install --global Devolutions.Terminal.App
dt --help
```

NuGet first restores a small pointer package, then selects the package matching
the current runtime identifier. Each RID package contains only its own
application payload, keeping every package below NuGet.org's package-size
limit. The installed command is `dt`; no `wt.exe` alias or Windows Terminal
compatibility shim is installed.
