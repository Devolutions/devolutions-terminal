# Devolutions Terminal NuGet distribution

`Devolutions.Terminal.App` contains self-contained NativeAOT distributions for
Windows (`win-x64`, `win-arm64`), Linux (`linux-x64`, `linux-arm64`), and macOS
(`osx-x64`, `osx-arm64`). The package is a distribution artifact rather than a
managed API package.

The package places the published files under
`runtimes/<runtime>/native/payload`. When referenced by an SDK-style project, the
included MSBuild targets copy the matching runtime files into the consuming
project's output. The command-line executable is `dt.exe` on Windows and `dt`
on Linux and macOS; no `wt.exe` alias or Windows Terminal compatibility shim is
installed.
