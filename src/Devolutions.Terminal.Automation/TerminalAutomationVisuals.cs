using System.Diagnostics;
using System.Text;
using Avalonia.Input;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal.Automation;

public sealed partial class TerminalAutomationService
{
    public TerminalAutomationCapture Capture(string id, TerminalAutomationRegion? region = null, bool includeCursor = false) =>
        GetSession(id).Capture(region, includeCursor);

    public Task<TerminalAutomationVisualWaitResult> WaitForChangeAsync(
        string id, string hash, TimeSpan timeout, TerminalAutomationRegion? region = null,
        CancellationToken cancellationToken = default) =>
        GetSession(id).WaitForChangeAsync(hash, timeout, region, cancellationToken);

    public Task<TerminalAutomationVisualWaitResult> ProbeAsync(
        string id, TerminalAutomationInput input, TimeSpan timeout, TerminalAutomationRegion? region = null,
        CancellationToken cancellationToken = default) =>
        GetSession(id).ProbeAsync(input, timeout, region, cancellationToken);

    public Task<TerminalAutomationPerformance> MeasureAsync(
        string id, int durationMilliseconds = 1_000, int intervalMilliseconds = 16,
        TerminalAutomationRegion? region = null, CancellationToken cancellationToken = default) =>
        GetSession(id).MeasureAsync(durationMilliseconds, intervalMilliseconds, region, cancellationToken);

    public void Mouse(string id, string eventType, int column, int row, int button = 0, string? modifiers = null) =>
        GetSession(id).Mouse(eventType, column, row, button, modifiers);

    public void KeyEvent(
        string id, string key, string eventType, string? modifiers = null, string? keySymbol = null) =>
        GetSession(id).KeyEvent(key, eventType, modifiers, keySymbol);

    private sealed partial class AutomationSession
    {
        public TerminalAutomationCapture Capture(TerminalAutomationRegion? region, bool includeCursor)
        {
            var (frame, png) = RenderFrame(region, includeCursor, encode: true);
            return new(frame, Convert.ToBase64String(png
                ?? throw new InvalidOperationException("The terminal capture has no PNG data.")));
        }

        private (TerminalAutomationFrame Frame, byte[]? Png) RenderFrame(
            TerminalAutomationRegion? region, bool includeCursor = false, bool encode = false)
        {
            TerminalSnapshot snapshot;
            ColorScheme scheme;
            long revision;
            lock (_gate)
            {
                ThrowIfFaulted();
                snapshot = _engine.CreateSnapshot();
                scheme = _engine.Scheme;
                revision = _revision;
            }

            return _visuals.Render(snapshot, scheme, revision, region, includeCursor, encode);
        }

        public async Task<TerminalAutomationVisualWaitResult> WaitForChangeAsync(
            string hash, TimeSpan timeout, TerminalAutomationRegion? region, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(hash);
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            {
                throw new ArgumentException("A 64-character SHA-256 frame hash is required.", nameof(hash));
            }

            ValidateVisualTimeout(timeout);
            var started = Stopwatch.GetTimestamp();
            var baseline = RenderFrame(region).Frame;
            return await WaitForChangeCoreAsync(hash, baseline, timeout, region, started, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<TerminalAutomationVisualWaitResult> ProbeAsync(
            TerminalAutomationInput input, TimeSpan timeout, TerminalAutomationRegion? region,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateVisualTimeout(timeout);
            cancellationToken.ThrowIfCancellationRequested();
            while (true)
            {
                TerminalAutomationFrame baseline;
                lock (_gate)
                {
                    EnsureRunning();
                }

                baseline = RenderFrame(region).Frame;
                Task write;
                lock (_gate)
                {
                    EnsureRunning();
                    if (_revision != baseline.Revision)
                    {
                        continue;
                    }

                    var inputText = EncodeProbeInput(input);
                    write = _connection.WriteAsync(Encoding.UTF8.GetBytes(inputText), cancellationToken).AsTask();
                }

                await write.ConfigureAwait(false);
                var started = Stopwatch.GetTimestamp();
                return await WaitForChangeCoreAsync(
                    baseline.Hash, baseline, timeout, region, started, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<TerminalAutomationVisualWaitResult> WaitForChangeCoreAsync(
            string hash, TerminalAutomationFrame baseline, TimeSpan timeout, TerminalAutomationRegion? region, long started,
            CancellationToken cancellationToken)
        {
            var frame = baseline;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                bool complete;
                long revision;
                lock (_gate)
                {
                    ThrowIfFaulted();
                    changed = _changed.Task;
                    complete = _exited && _outputDrained;
                    revision = _revision;
                }

                if (revision != frame.Revision)
                {
                    frame = RenderFrame(region).Frame;
                    lock (_gate)
                    {
                        if (_revision != frame.Revision)
                        {
                            continue;
                        }
                    }
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (!frame.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new(true, elapsed.TotalMilliseconds, frame);
                }

                if (complete || elapsed >= timeout)
                {
                    return new(false, elapsed.TotalMilliseconds, frame);
                }

                try
                {
                    await changed.WaitAsync(timeout - elapsed, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    while (true)
                    {
                        frame = RenderFrame(region).Frame;
                        lock (_gate)
                        {
                            if (_revision != frame.Revision)
                            {
                                continue;
                            }
                        }

                        return new(!frame.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase),
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds, frame);
                    }
                }
            }
        }

        public async Task<TerminalAutomationPerformance> MeasureAsync(
            int durationMilliseconds, int intervalMilliseconds, TerminalAutomationRegion? region,
            CancellationToken cancellationToken)
        {
            if (durationMilliseconds is < 16 or > 30_000 || intervalMilliseconds is < 16 or > 1_000 ||
                intervalMilliseconds > durationMilliseconds ||
                (durationMilliseconds + intervalMilliseconds - 1) / intervalMilliseconds + 1 > 600)
            {
                throw new ArgumentOutOfRangeException(nameof(durationMilliseconds),
                    "Use a duration of 16..30000 ms and interval of 16..1000 ms, no longer than the duration, with at most 600 samples.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            TerminalAutomationFrame initial;
            long outputBytes;
            long parseTicks;
            long started;
            initial = RenderFrame(region).Frame;

            lock (_gate)
            {
                ThrowIfFaulted();
                outputBytes = _outputBytes;
                parseTicks = _parseTicks;
                started = Stopwatch.GetTimestamp();
            }

            var samples = new List<TerminalAutomationFrameSample> { new(0, initial) };
            var intervals = new List<double>();
            var lastHash = initial.Hash;
            var lastChange = 0d;
            var longest = 0d;
            while (samples.Count < 600)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = durationMilliseconds - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(Math.Min(intervalMilliseconds, remaining))),
                        cancellationToken).ConfigureAwait(false);
                }

                var frame = RenderFrame(region).Frame;
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                samples.Add(new(elapsed, frame));
                if (frame.Hash != lastHash)
                {
                    var gap = elapsed - lastChange;
                    intervals.Add(gap);
                    longest = Math.Max(longest, gap);
                    lastChange = elapsed;
                    lastHash = frame.Hash;
                }

                if (elapsed >= durationMilliseconds)
                {
                    break;
                }
            }

            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            longest = Math.Max(longest, duration - lastChange);
            lock (_gate)
            {
                ThrowIfFaulted();
                outputBytes = _outputBytes - outputBytes;
                parseTicks = _parseTicks - parseTicks;
            }

            return new(duration, intervalMilliseconds, intervals.Count, intervals.Count * 1_000 / duration,
                longest, outputBytes, parseTicks * 1_000d / Stopwatch.Frequency,
                Summarize(samples.Select(static sample => sample.Frame.RenderMilliseconds)),
                intervals.Count == 0 ? null : Summarize(intervals), samples);
        }

        private static TerminalAutomationTiming Summarize(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            var middle = sorted.Length / 2;
            return new(sorted[0],
                sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle],
                sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
        }

        private static void ValidateVisualTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero and at most five minutes.");
            }
        }

        private string EncodeProbeInput(TerminalAutomationInput input)
        {
            switch (input.Type)
            {
                case "text":
                    EnsureRunning();
                    return input.Text ?? throw new ArgumentException("Input text is required.");
                case "paste":
                    EnsureRunning();
                    return _engine.WrapPaste(input.Text ?? throw new ArgumentException("Paste text is required."));
                case "key":
                    return EncodeKeyEvent(
                        input.Key ?? throw new ArgumentException("Input key is required."),
                        input.Event ?? "press", input.Modifiers, input.KeySymbol);
                case "click":
                    if (input.Button is < 0 or > 2)
                    {
                        throw new ArgumentOutOfRangeException(nameof(input), "Mouse button must be 0..2.");
                    }

                    EnsureRunning();
                    if (!_engine.MouseTracking)
                    {
                        throw new InvalidOperationException("The application has not enabled terminal mouse tracking.");
                    }

                    if (_pressedMouseButton >= 0)
                    {
                        throw new InvalidOperationException("Release the held mouse button before sending a click.");
                    }

                    ValidateMouseCoordinates(input.Column, input.Row);
                    return TerminalInteractionModel.BuildMouseSequence(
                               input.Button, input.Column, input.Row, false, _engine.SgrMouse, KeyModifiers.None) +
                           TerminalInteractionModel.BuildMouseSequence(
                               input.Button, input.Column, input.Row, true, _engine.SgrMouse, KeyModifiers.None);
                case "mouse":
                    return EncodeMouse(input.Event ?? throw new ArgumentException("Mouse event is required."),
                        input.Column, input.Row, input.Button, input.Modifiers);
                default:
                    throw new ArgumentException("Input type must be text, paste, key, click, or mouse.");
            }
        }

        public void KeyEvent(string key, string eventType, string? modifiers, string? keySymbol)
        {
            lock (_gate)
            {
                EnsureRunning();
                _connection.Write(EncodeKeyEvent(key, eventType, modifiers, keySymbol));
            }
        }

        private string EncodeKeyEvent(string key, string eventType, string? modifiers, string? keySymbol)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!TryResolveKey(key, out var resolved))
            {
                throw new ArgumentException($"Unsupported terminal key '{key}'.", nameof(key));
            }

            var type = eventType switch
            {
                "press" => TerminalKeyEventType.Press,
                "repeat" => TerminalKeyEventType.Repeat,
                "release" => TerminalKeyEventType.Release,
                _ => throw new ArgumentException("Key event must be press, repeat, or release.", nameof(eventType)),
            };
            var parsedModifiers = ParseModifiers(modifiers);
            var symbol = ResolveKeySymbol(resolved, parsedModifiers, keySymbol);
            var sequence = KeyMapper.ToVt(resolved, parsedModifiers, default, symbol, _engine.InputMode, type);
            if (sequence is not null)
            {
                return sequence;
            }

            if (type is TerminalKeyEventType.Press or TerminalKeyEventType.Repeat && symbol is not null)
            {
                return symbol;
            }

            throw new InvalidOperationException("The active terminal keyboard mode cannot encode this key event.");
        }

        public void Mouse(string eventType, int column, int row, int button, string? modifiers)
        {
            lock (_gate)
            {
                EnsureRunning();
                _connection.Write(EncodeMouse(eventType, column, row, button, modifiers));
            }
        }

        private string EncodeMouse(string eventType, int column, int row, int button, string? modifiers)
        {
            if (button is < 0 or > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(button), "Mouse button must be 0..2.");
            }

            var parsedModifiers = ParseModifiers(modifiers);
            if ((parsedModifiers & KeyModifiers.Meta) != 0)
            {
                throw new ArgumentException("Terminal mouse reports support Shift, Control, and Alt modifiers.", nameof(modifiers));
            }

            EnsureRunning();
            if (!_engine.MouseTracking)
            {
                throw new InvalidOperationException("The application has not enabled terminal mouse tracking.");
            }

            ValidateMouseCoordinates(column, row);
            var code = eventType switch
            {
                "press" when _pressedMouseButton < 0 => button,
                "release" when _pressedMouseButton == button => button,
                "move" when _engine.MouseTrackingMode == TerminalMouseTrackingMode.AllMotion ||
                            (_engine.MouseTrackingMode == TerminalMouseTrackingMode.ButtonEvent && _pressedMouseButton >= 0)
                    => 32 + (_pressedMouseButton >= 0 ? _pressedMouseButton : 3),
                "scroll-up" => 64,
                "scroll-down" => 65,
                "scroll-left" => 66,
                "scroll-right" => 67,
                _ => throw new InvalidOperationException("Mouse event is invalid for the current tracking mode or pressed-button state."),
            };
            if (eventType == "press")
            {
                _pressedMouseButton = button;
            }
            else if (eventType == "release")
            {
                _pressedMouseButton = -1;
            }

            return TerminalInteractionModel.BuildMouseSequence(
                code, column, row, eventType == "release", _engine.SgrMouse, parsedModifiers);
        }

        private void ValidateMouseCoordinates(int column, int row)
        {
            if (column < 0 || column >= _engine.Columns || row < 0 || row >= _engine.Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(column), "Mouse coordinates must be inside the terminal grid.");
            }
        }

        private static KeyModifiers ParseModifiers(string? modifiers)
        {
            var parsed = KeyModifiers.None;
            const KeyModifiers allowed = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
            if ((!string.IsNullOrWhiteSpace(modifiers) &&
                 !Enum.TryParse(modifiers, ignoreCase: true, out parsed)) || (parsed & ~allowed) != 0)
            {
                throw new ArgumentException($"Unsupported key modifiers '{modifiers}'.", nameof(modifiers));
            }

            return parsed;
        }
    }
}
