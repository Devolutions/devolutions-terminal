# Devolutions Terminal NuGet distribution

`Devolutions.Terminal` contains self-contained NativeAOT Windows distributions
for `win-x64` and `win-arm64`. The package is a distribution artifact rather
than a managed API package.

The package places the published files under
`runtimes/<runtime>/native/dt`. When referenced by an SDK-style project, the
included MSBuild targets copy the matching runtime files into the consuming
project's output. The command-line executable remains `dt.exe`; no `wt.exe`
alias or Windows Terminal compatibility shim is installed.
