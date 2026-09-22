import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();
const exports = await dotnetRuntime.getAssemblyExports(config.mainAssemblyName);
const probe = exports?.Devolutions?.Terminal?.Browser?.BrowserTerminalProbe;
if (!probe) {
    console.error('BrowserTerminalProbe export was not found', exports);
    throw new Error('BrowserTerminalProbe export was not found');
}

globalThis.__dterm = {
    ready: () => probe.IsReady(),
    getText: () => probe.GetScreenText(),
    sendInput: (text) => probe.SendInput(text ?? ""),
    tabCount: () => probe.GetTabCount(),
    title: () => probe.GetTitle(),
};

await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
