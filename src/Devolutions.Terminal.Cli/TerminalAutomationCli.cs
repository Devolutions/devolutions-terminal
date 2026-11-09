using Devolutions.Terminal.Automation;
using System.Text.Json;

namespace Devolutions.Terminal.Cli;

internal static class TerminalAutomationCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                WriteHelp();
                return 0;
            }

            if (args[0] == "host")
            {
                if (args.Length == 2 && args[1] == "--detach")
                {
                    AutomationHostLauncher.DetachFromParentSession();
                }
                else
                {
                    RequireArgumentCount(args, 1, 1);
                }

                return await RunHostAsync().ConfigureAwait(false);
            }

            var request = ParseRequest(args);
            var (executable, prefix) = AutomationHostLauncher.GetCurrentProcessCommand();
            var ensureHost = (CancellationToken cancellationToken) => AutomationHostLauncher.EnsureRunningAsync(
                executable, [.. prefix, "automation", "host", "--detach"], cancellationToken);
            await ensureHost(CancellationToken.None).ConfigureAwait(false);

            var client = new AutomationIpcClient(ensureHost);
            object response;
            if (request.Type == "capture")
            {
                var capture = await client.CaptureAsync(request).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.GetFullPath(args[2]),
                    Convert.FromBase64String(capture.PngBase64)).ConfigureAwait(false);
                response = capture.Frame;
            }
            else
            {
                response = await DispatchAsync(client, request).ConfigureAwait(false);
            }
            if (response is not null)
            {
                await Console.Out.WriteLineAsync(Serialize(response)).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          KeyNotFoundException or IOException or TimeoutException or JsonException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            await Console.Error.WriteLineAsync($"dt automation: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunHostAsync()
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await using var service = new TerminalAutomationService();
            await new AutomationIpcHost(service).RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static AutomationIpcRequest ParseRequest(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException("An automation command is required.", nameof(args));
        }

        var type = args[0];
        switch (type)
        {
            case "start":
                return ParseStart(args);

            case "list":
                RequireArgumentCount(args, 1, 1);
                return new AutomationIpcRequest { Type = "list" };

            case "observe":
                RequireArgumentCount(args, 2, 2);
                return new AutomationIpcRequest
                {
                    Type = "observe",
                    SessionId = Positional(args, 1, "session id"),
                };

            case "capture":
                RequireArgumentCount(args, 3, 4);
                _ = Path.GetFullPath(Positional(args, 2, "output PNG path"));
                return new AutomationIpcRequest
                {
                    Type = "capture",
                    SessionId = Positional(args, 1, "session id"),
                    Region = RegionArgument(args, 3),
                };

            case "wait-change":
            case "probe":
                RequireArgumentCount(args, 3, 5);
                return new AutomationIpcRequest
                {
                    Type = type,
                    SessionId = Positional(args, 1, "session id"),
                    Hash = type == "wait-change" ? Positional(args, 2, "frame hash") : null,
                    Input = type == "probe"
                        ? JsonSerializer.Deserialize(Positional(args, 2, "input JSON"),
                            AutomationJsonContext.Default.TerminalAutomationInput)
                            ?? throw new ArgumentException("Input JSON is required.")
                        : null,
                    TimeoutMilliseconds = args.Length > 3 ? IntegerArgument(args, 3, "timeout milliseconds") : 10_000,
                    Region = RegionArgument(args, 4),
                };

            case "measure":
                RequireArgumentCount(args, 2, 5);
                return new AutomationIpcRequest
                {
                    Type = "measure",
                    SessionId = Positional(args, 1, "session id"),
                    DurationMilliseconds = args.Length > 2 ? IntegerArgument(args, 2, "duration milliseconds") : 1_000,
                    IntervalMilliseconds = args.Length > 3 ? IntegerArgument(args, 3, "interval milliseconds") : 16,
                    Region = RegionArgument(args, 4),
                };

            case "mouse":
                RequireArgumentCount(args, 5, 7);
                return new AutomationIpcRequest
                {
                    Type = "mouse",
                    SessionId = Positional(args, 1, "session id"),
                    Event = Positional(args, 2, "mouse event"),
                    Column = IntegerArgument(args, 3, "column"),
                    Row = IntegerArgument(args, 4, "row"),
                    Button = args.Length > 5 ? IntegerArgument(args, 5, "button") : 0,
                    Modifiers = args.Length > 6 ? args[6] : null,
                };

            case "key-event":
                RequireArgumentCount(args, 4, 6);
                return new AutomationIpcRequest
                {
                    Type = "key-event",
                    SessionId = Positional(args, 1, "session id"),
                    Key = Positional(args, 2, "key"),
                    Event = Positional(args, 3, "key event"),
                    Modifiers = args.Length > 4 ? args[4] : null,
                    KeySymbol = args.Length > 5 ? args[5] : null,
                };

            case "text":
            case "paste":
                RequireArgumentCount(args, 3, 3);
                return new AutomationIpcRequest
                {
                    Type = type == "text" ? "send-text" : "paste",
                    SessionId = Positional(args, 1, "session id"),
                    Text = TextArgument(args, 2),
                };

            case "key":
                RequireArgumentCount(args, 3, 5);
                return new AutomationIpcRequest
                {
                    Type = "send-key",
                    SessionId = Positional(args, 1, "session id"),
                    Key = Positional(args, 2, "key"),
                    Modifiers = args.Length > 3 ? args[3] : null,
                    KeySymbol = args.Length > 4 ? args[4] : null,
                };

            case "click":
                RequireArgumentCount(args, 4, 5);
                return new AutomationIpcRequest
                {
                    Type = "click",
                    SessionId = Positional(args, 1, "session id"),
                    Column = IntegerArgument(args, 2, "column"),
                    Row = IntegerArgument(args, 3, "row"),
                    Button = args.Length > 4 ? IntegerArgument(args, 4, "button") : 0,
                };

            case "resize":
                RequireArgumentCount(args, 4, 4);
                return new AutomationIpcRequest
                {
                    Type = "resize",
                    SessionId = Positional(args, 1, "session id"),
                    Columns = IntegerArgument(args, 2, "columns"),
                    Rows = IntegerArgument(args, 3, "rows"),
                };

            case "wait":
                RequireArgumentCount(args, 3, 4);
                return new AutomationIpcRequest
                {
                    Type = "wait-text",
                    SessionId = Positional(args, 1, "session id"),
                    Text = TextArgument(args, 2),
                    TimeoutMilliseconds = args.Length > 3
                        ? IntegerArgument(args, 3, "timeout milliseconds")
                        : 10_000,
                };

            case "close":
                RequireArgumentCount(args, 2, 2);
                return new AutomationIpcRequest
                {
                    Type = "close",
                    SessionId = Positional(args, 1, "session id"),
                };

            case "stop":
                RequireArgumentCount(args, 1, 1);
                return new AutomationIpcRequest { Type = "stop" };

            default:
                throw new ArgumentException($"Unknown automation command '{type}'. Use --help for usage.");
        }
    }

    private static async Task<object> DispatchAsync(AutomationIpcClient client, AutomationIpcRequest request) =>
        request.Type switch
        {
            "start" => await client.StartAsync(request).ConfigureAwait(false),
            "list" => await client.ListAsync().ConfigureAwait(false),
            "observe" => await client.ObserveAsync(request).ConfigureAwait(false),
            "wait-text" => await client.WaitAsync(request).ConfigureAwait(false),
            "wait-change" => await client.WaitForChangeAsync(request).ConfigureAwait(false),
            "probe" => await client.ProbeAsync(request).ConfigureAwait(false),
            "measure" => await client.MeasureAsync(request).ConfigureAwait(false),
            _ => await SendActionAsync(client, request).ConfigureAwait(false),
        };

    private static async Task<object> SendActionAsync(AutomationIpcClient client, AutomationIpcRequest request)
    {
        await client.SendAsync(request).ConfigureAwait(false);
        return new TerminalAutomationCommandResult(true);
    }

    private static string Serialize(object value) =>
        value switch
        {
            TerminalAutomationSessionInfo session => AutomationIpcProtocol.SerializeJson(session),
            IReadOnlyList<TerminalAutomationSessionInfo> sessions => AutomationIpcProtocol.SerializeJson(sessions),
            TerminalAutomationSnapshot snapshot => AutomationIpcProtocol.SerializeJson(snapshot),
            TerminalAutomationWaitResult wait => AutomationIpcProtocol.SerializeJson(wait),
            TerminalAutomationCommandResult command => AutomationIpcProtocol.SerializeJson(command),
            TerminalAutomationFrame frame => AutomationIpcProtocol.SerializeJson(frame),
            TerminalAutomationVisualWaitResult visual => AutomationIpcProtocol.SerializeJson(visual),
            TerminalAutomationPerformance performance => AutomationIpcProtocol.SerializeJson(performance),
            _ => throw new InvalidOperationException($"Unsupported automation output '{value.GetType().Name}'."),
        };

    private static TerminalAutomationRegion? RegionArgument(string[] args, int index) =>
        args.Length <= index ? null :
            JsonSerializer.Deserialize(args[index], AutomationJsonContext.Default.TerminalAutomationRegion)
            ?? throw new ArgumentException("Region JSON is required.");

    private static AutomationIpcRequest ParseStart(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var name = args[index];
            if (name is not ("--command" or "--cwd" or "--cols" or "--rows"))
            {
                throw new ArgumentException($"Unknown start option '{name}'.");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name} requires a value.");
            }

            if (!options.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"{name} may only be specified once.");
            }
        }

        if (!options.TryGetValue("--command", out var commandLine) || string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("start requires --command <command-line>.");
        }

        if (options.TryGetValue("--cwd", out var workingDirectory) && string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("--cwd requires a nonempty path.");
        }

        return new AutomationIpcRequest
        {
            Type = "start",
            CommandLine = commandLine,
            WorkingDirectory = workingDirectory,
            Columns = IntegerOption(options, "--cols", 120),
            Rows = IntegerOption(options, "--rows", 30),
        };
    }

    private static int IntegerOption(IReadOnlyDictionary<string, string> options, string name, int defaultValue)
    {
        return !options.TryGetValue(name, out var value)
            ? defaultValue
            : int.TryParse(value, out var parsed)
                ? parsed
                : throw new ArgumentException($"{name} must be an integer.");
    }

    private static void RequireArgumentCount(IReadOnlyList<string> args, int minimum, int maximum)
    {
        if (args.Count < minimum)
        {
            throw new ArgumentException($"Not enough arguments for '{args[0]}'.");
        }

        if (args.Count > maximum)
        {
            throw new ArgumentException($"Unexpected argument '{args[maximum]}' for '{args[0]}'.");
        }
    }

    private static string Positional(IReadOnlyList<string> args, int index, string name) =>
        args.Count > index && !string.IsNullOrWhiteSpace(args[index])
            ? args[index]
            : throw new ArgumentException($"A {name} is required.");

    private static string TextArgument(IReadOnlyList<string> args, int index) =>
        args.Count > index ? args[index] : throw new ArgumentException("Text is required.");

    private static int IntegerArgument(IReadOnlyList<string> args, int index, string name) =>
        args.Count > index && int.TryParse(args[index], out var value)
            ? value
            : throw new ArgumentException($"A valid {name} is required.");

    private static void WriteHelp()
    {
        Console.Out.WriteLine("dt automation <command>");
        Console.Out.WriteLine("  start --command <command-line> [--cwd <path>] [--cols <n>] [--rows <n>]");
        Console.Out.WriteLine("  list | observe <id> | text <id> <text> | paste <id> <text>");
        Console.Out.WriteLine("  capture <id> <output.png> [region-json]");
        Console.Out.WriteLine("  wait-change <id> <hash> [timeout-ms] [region-json]");
        Console.Out.WriteLine("  probe <id> <input-json> [timeout-ms] [region-json]");
        Console.Out.WriteLine("  measure <id> [duration-ms] [interval-ms] [region-json]");
        Console.Out.WriteLine("  mouse <id> <event> <column> <row> [button] [modifiers]");
        Console.Out.WriteLine("  key-event <id> <key> <press|repeat|release> [modifiers] [key-symbol]");
        Console.Out.WriteLine("  key <id> <key> [modifiers] [key-symbol] | click <id> <column> <row> [button]");
        Console.Out.WriteLine("  resize <id> <columns> <rows> | wait <id> <text> [timeout-ms]");
        Console.Out.WriteLine("  close <id> | stop");
        Console.Out.WriteLine("Each command emits JSON; the host runs without opening a terminal window.");
    }
}
