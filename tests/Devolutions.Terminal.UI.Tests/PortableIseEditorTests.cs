using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Iseberg;
using Iseberg.Core;
using Iseberg.Editor;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

public sealed class PortableIseEditorTests(PortableIseEditorRuntimeFixture runtime) : IClassFixture<PortableIseEditorRuntimeFixture>
{
    private Task<ScriptAnalysis> AnalyzeAsync(string text, string? path = null) =>
        runtime.Analysis.AnalyzeAsync(text, path, CancellationToken.None);

    private static void SetScriptAnalysis(ScriptTab script, ScriptAnalysis analysis)
    {
        var text = script.Document.Text;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ScriptTab).GetProperty("Analysis", flags)!.SetValue(script, analysis);
        typeof(ScriptTab).GetProperty("AnalysisText", flags)!.SetValue(script, text);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 4, 7)]
    [InlineData(int.MaxValue, 0, int.MaxValue)]
    [InlineData(int.MaxValue - 1, 1, int.MaxValue)]
    public void TextSpanKeepsUtf16StartLengthAndCheckedEnd(int start, int length, int end)
    {
        var span = new EditorTextSpan(start, length);
        Assert.Equal(start, span.Start);
        Assert.Equal(length, span.Length);
        Assert.Equal(end, span.End);
    }

    [Theory]
    [InlineData(-1, 0, "start")]
    [InlineData(0, -1, "length")]
    [InlineData(int.MaxValue, 1, "length")]
    [InlineData(int.MaxValue - 1, 2, "length")]
    public void TextSpanRejectsNegativeAndOverflowingRanges(int start, int length, string parameter)
    {
        Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(() => new EditorTextSpan(start, length)).ParamName);
    }

    [AvaloniaFact]
    public void CaptureTextPinsSelectionCurrentLineAndIndependentDocumentSnapshots()
    {
        var document = new TextDocument("first\r\ncafé 😀\nlast");
        using var editor = new PowerShellEditorControl(document);
        editor.CaretOffset = 10;
        var line = editor.CaptureText(EditorTextScope.SelectionOrCurrentLine);
        Assert.Equal("café 😀", line.Text);
        Assert.Equal(new EditorTextSpan(7, 7), line.Span);
        editor.Select(new(8, 3));
        var selection = editor.CaptureText(EditorTextScope.SelectionOrCurrentLine);
        Assert.Equal("afé", selection.Text);
        Assert.Equal(new EditorTextSpan(8, 3), selection.Span);
        var full = editor.CaptureText();
        Assert.Equal("first\r\ncafé 😀\nlast", full.Text);
        Assert.Equal(new EditorTextSpan(0, 19), full.Span);
        document.Insert(0, "new ");
        Assert.Equal("afé", selection.Text);
        Assert.Equal("first\r\ncafé 😀\nlast", full.Text);
        Assert.True(editor.DocumentVersion > full.Version);
        Assert.Equal("new first\r\ncafé 😀\nlast", document.Text);
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(4)]
    public void CaretAndSelectionRejectOutOfDocumentRangesWithoutMutation(int offset)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.CaretOffset = 2;
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.CaretOffset = offset);
        Assert.Equal(2, editor.CaretOffset);
        if (offset >= 0)
            Assert.Throws<ArgumentOutOfRangeException>(() => editor.Select(new(offset, 0)));
        Assert.Equal("abc", editor.Document.Text);
        Assert.Equal(new EditorTextSpan(0, 0), editor.Selection);
        editor.CaretOffset = 3;
        Assert.Equal(3, editor.CaretOffset);
    }

    [AvaloniaTheory]
    [InlineData("edit")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public async Task AnalysisRejectsAnOldProviderResultAfterDocumentOrLifetimeChange(string change)
    {
        var document = new TextDocument("abc");
        using var editor = new PowerShellEditorControl(document);
        var provider = new HeldAnalysis();
        editor.AnalysisProvider = provider;
        var pending = editor.AnalyzeAsync();
        var request = await provider.Request.Task;
        Assert.Equal("abc", request.Text);
        Assert.Equal(EditorAnalysisState.Pending, editor.Analysis.State);
        if (change == "edit") document.Insert(0, "new ");
        else if (change == "replace") editor.Document = new TextDocument("replacement");
        else editor.Dispose();
        provider.Result.SetResult(new(request.Version, EditorAnalysisState.Available,
            [new("Old", "must not publish", EditorDiagnosticSeverity.Error, new(0, 1))]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(editor.Analysis.Diagnostics);
        Assert.NotEqual(EditorAnalysisState.Available, editor.Analysis.State);
        Assert.Equal(change == "edit" ? "new abc" : "abc", document.Text);
        if (change == "replace") Assert.Equal("replacement", editor.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData("wrong-version")]
    [InlineData("pending")]
    [InlineData("default-diagnostics")]
    [InlineData("unavailable-with-diagnostics")]
    [InlineData("null-code")]
    [InlineData("null-message")]
    [InlineData("invalid-severity")]
    [InlineData("null-diagnostic")]
    [InlineData("outside-document")]
    public async Task AnalysisValidatesEveryProviderResultPartitionWithoutEditing(string kind)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.AnalysisProvider = new AnalysisFunction(request =>
        {
            var diagnostic = new EditorDiagnostic("E1", "bad token", EditorDiagnosticSeverity.Error, new(1, 1));
            return kind switch
            {
                "wrong-version" => new(request.Version + 1, EditorAnalysisState.Available, []),
                "pending" => new(request.Version, EditorAnalysisState.Pending, []),
                "default-diagnostics" => new(request.Version, EditorAnalysisState.Available, default),
                "unavailable-with-diagnostics" => new(request.Version, EditorAnalysisState.Unavailable, [diagnostic]),
                "null-code" => new(request.Version, EditorAnalysisState.Available, [diagnostic with { Code = null! }]),
                "null-message" => new(request.Version, EditorAnalysisState.Available, [diagnostic with { Message = null! }]),
                "invalid-severity" => new(request.Version, EditorAnalysisState.Available, [diagnostic with { Severity = (EditorDiagnosticSeverity)99 }]),
                "null-diagnostic" => new(request.Version, EditorAnalysisState.Available, [null!]),
                _ => new(request.Version, EditorAnalysisState.Available, [diagnostic with { Span = new(3, 1) }])
            };
        });
        if (kind == "outside-document")
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editor.AnalyzeAsync());
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => editor.AnalyzeAsync());
        Assert.Equal(EditorAnalysisState.Failed, editor.Analysis.State);
        Assert.Empty(editor.Analysis.Diagnostics);
        Assert.Equal("abc", editor.Document.Text);
    }

    [AvaloniaFact]
    public async Task AnalysisPublishesExactDiagnosticsAndClearsThemWhenProviderBecomesUnavailable()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        var observed = new List<EditorAnalysisState>();
        editor.AnalysisChanged += (_, _) => observed.Add(editor.Analysis.State);
        editor.AnalysisProvider = new AnalysisFunction(request => new(request.Version, EditorAnalysisState.Available,
            [new("E1", "bad token", EditorDiagnosticSeverity.Warning, new(1, 2))]));
        var result = await editor.AnalyzeAsync();
        Assert.Equal(new EditorDiagnostic("E1", "bad token", EditorDiagnosticSeverity.Warning, new(1, 2)),
            Assert.Single(result.Diagnostics));
        Assert.Same(result, editor.Analysis);
        Assert.Contains(EditorAnalysisState.Pending, observed);
        Assert.Equal(EditorAnalysisState.Available, observed[^1]);
        editor.AnalysisProvider = null;
        var unavailable = await editor.AnalyzeAsync();
        Assert.Equal(EditorAnalysisState.Unavailable, unavailable.State);
        Assert.Empty(unavailable.Diagnostics);
        Assert.Equal("abc", editor.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData("edit")]
    [InlineData("caret")]
    [InlineData("replace")]
    [InlineData("detach")]
    [InlineData("dispose")]
    public async Task CompletionRejectsStaleEditCaretDocumentDetachAndDispose(string change)
    {
        var document = new TextDocument("abc");
        using var editor = new PowerShellEditorControl(document);
        var owner = new Window { Content = editor, Width = 600, Height = 400 };
        try
        {
            owner.Show();
            editor.CaretOffset = 2;
            var provider = new HeldCompletion();
            editor.CompletionProvider = provider;
            var pending = editor.RequestCompletionAsync();
            var request = await provider.Request.Task;
            Assert.Equal("abc", request.Text);
            Assert.Equal(2, request.CaretOffset);
            if (change == "edit") document.Insert(0, "new ");
            else if (change == "caret") editor.CaretOffset = 1;
            else if (change == "replace") editor.Document = new TextDocument("replacement");
            else if (change == "detach") owner.Content = null;
            else editor.Dispose();
            provider.Result.SetResult(new(request.Version, new(0, 2), [new("old", "Old", "stale description")]));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(editor.IsCompletionOpen);
            Assert.Equal(change == "edit" ? "new abc" : "abc", document.Text);
            if (change == "caret") Assert.Equal(1, editor.CaretOffset);
            if (change == "replace") Assert.Equal("replacement", editor.Document.Text);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("wrong-version")]
    [InlineData("outside-document")]
    [InlineData("default-items")]
    [InlineData("null-item")]
    [InlineData("null-text")]
    [InlineData("null-display")]
    public async Task CompletionValidatesResultRangesVersionAndItemsWithoutMutation(string kind)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.CaretOffset = 2;
        editor.CompletionProvider = new CompletionFunction(request => kind switch
        {
            "wrong-version" => new(request.Version + 1, new(0, 1), [new("x", "X")]),
            "outside-document" => new(request.Version, new(3, 1), [new("x", "X")]),
            "default-items" => new(request.Version, new(0, 1), default),
            "null-item" => new(request.Version, new(0, 1), [null!]),
            "null-text" => new(request.Version, new(0, 1), [new(null!, "X")]),
            _ => new(request.Version, new(0, 1), [new("x", null!)])
        });
        if (kind == "outside-document")
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editor.RequestCompletionAsync());
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => editor.RequestCompletionAsync());
        Assert.Equal("abc", editor.Document.Text);
        Assert.Equal(2, editor.CaretOffset);
        Assert.False(editor.IsCompletionOpen);
    }

    [AvaloniaFact]
    public async Task EmptyCompletionIsAValidResultButDoesNotOpenOrEdit()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.CompletionProvider = new CompletionFunction(request => new(request.Version, new(3, 0), []));
        var owner = new Window { Content = editor, Width = 600, Height = 400 };
        try
        {
            owner.Show();
            editor.CaretOffset = 3;
            var result = await editor.RequestCompletionAsync();
            Assert.Equal(new EditorTextSpan(3, 0), result.ReplacementSpan);
            Assert.Empty(result.Items);
            await editor.ShowCompletionAsync();
            Assert.False(editor.IsCompletionOpen);
            Assert.Equal("abc", editor.Document.Text);
            Assert.Equal(3, editor.CaretOffset);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaFact]
    public void ApplyCompletionReplacesOnlyItsSpanAndRejectsReuseAndReadOnly()
    {
        var document = new TextDocument("prefix ab suffix");
        using var editor = new PowerShellEditorControl(document);
        var completion = new EditorCompletionList(editor.DocumentVersion, new(7, 2),
            [new("alpha", "Alpha", "description")]);
        editor.IsReadOnly = true;
        Assert.Equal("The editor is read-only.", Assert.Throws<InvalidOperationException>(
            () => editor.ApplyCompletion(completion, 0)).Message);
        Assert.Equal("prefix ab suffix", document.Text);
        editor.IsReadOnly = false;
        editor.ApplyCompletion(completion, 0);
        Assert.Equal("prefix alpha suffix", document.Text);
        Assert.Equal(12, editor.CaretOffset);
        Assert.Equal("The completion snapshot is stale.", Assert.Throws<InvalidOperationException>(
            () => editor.ApplyCompletion(completion, 0)).Message);
        Assert.Equal("prefix alpha suffix", document.Text);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyCompletionRejectsProtectedInsertionOrPartialDeletion(bool denyInsertion)
    {
        var document = new TextDocument("prefix ab suffix");
        using var editor = new PowerShellEditorControl(document);
        editor.TextEditor.TextArea.ReadOnlySectionProvider = new ProtectedSection(denyInsertion);
        var completion = new EditorCompletionList(editor.DocumentVersion, new(7, 2), [new("alpha", "Alpha")]);
        var version = editor.DocumentVersion;
        Assert.Equal("The completion would replace protected text.", Assert.Throws<InvalidOperationException>(
            () => editor.ApplyCompletion(completion, 0)).Message);
        Assert.Equal("prefix ab suffix", document.Text);
        Assert.Equal(version, editor.DocumentVersion);
        Assert.Equal(0, editor.CaretOffset);
    }

    [AvaloniaFact]
    public void SnapshotValidityTracksAttachmentEditsReplacementAndDetachLifetime()
    {
        var document = new TextDocument("abc");
        using var editor = new PowerShellEditorControl(document);
        var version = editor.DocumentVersion;
        var lifetime = editor.EditorLifetime;
        Assert.False(editor.IsSnapshotCurrent(version, lifetime));
        var owner = new Window { Content = editor, Width = 600, Height = 400 };
        try
        {
            owner.Show();
            Assert.True(editor.IsSnapshotCurrent(version, lifetime));
            document.Insert(3, "d");
            Assert.False(editor.IsSnapshotCurrent(version, lifetime));
            version = editor.DocumentVersion;
            Assert.True(editor.IsSnapshotCurrent(version, lifetime));
            editor.Document = new TextDocument("replacement");
            Assert.False(editor.IsSnapshotCurrent(version, lifetime));
            version = editor.DocumentVersion;
            owner.Content = null;
            Assert.False(editor.IsSnapshotCurrent(version, lifetime));
            Assert.Equal(lifetime + 1, editor.EditorLifetime);
            owner.Content = editor;
            Assert.True(editor.IsSnapshotCurrent(version, editor.EditorLifetime));
            editor.Dispose();
            Assert.False(editor.IsSnapshotCurrent(version, lifetime + 1));
            Assert.Equal("abcd", document.Text);
            Assert.Throws<ObjectDisposedException>(() => editor.CaptureText());
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [Theory]
    [InlineData("Dark Console, Light Editor (default)", "#A82D00", "#696969", "#800080", "#006400", "#8B0000")]
    [InlineData("Light Console, Dark Editor", "#FF4500", "#D3D3D3", "#FFE4C4", "#98FB98", "#DB7093")]
    [InlineData("Dark Console, Dark Editor", "#FF4500", "#D3D3D3", "#FFE4C4", "#98FB98", "#DB7093")]
    [InlineData("Light Console, Light Editor", "#FF4500", "#A9A9A9", "#800080", "#006400", "#8B0000")]
    [InlineData("Monochrome Green", "#00BF00", "#007F00", "#009F00", "#007F00", "#00DF00")]
    [InlineData("Presentation", "#FF4500", "#A9A9A9", "#800080", "#006400", "#8B0000")]
    public async Task OriginalBuiltInThemesRenderScriptColorSpans(string name, string variable, string op,
        string number, string comment, string literal)
    {
        var spans = PowerShellColorizer.GetColorSpans(await AnalyzeAsync("$x = 42 #ok\n'hi'"),
            EditorThemePresets.Original(name));
        Assert.Equal(new SyntaxColorSpan[]
        {
            new(0, 2, Color.Parse(variable)), new(3, 4, Color.Parse(op)), new(5, 7, Color.Parse(number)),
            new(8, 11, Color.Parse(comment)), new(12, 16, Color.Parse(literal))
        }, spans);
    }

    [Fact]
    public async Task XmlColorSpansUseXmlPaletteAndQuotedValueExtent()
    {
        var theme = new EditorTheme();
        theme.Colors["Xml.Tag"] = "#112233";
        theme.Colors["Xml.Attribute"] = "#445566";
        theme.Colors["Xml.Value"] = "#778899";
        Assert.Equal(new SyntaxColorSpan[]
        {
            new(1, 2, Color.Parse("#112233")), new(3, 4, Color.Parse("#445566")),
            new(5, 8, Color.Parse("#778899"))
        }, PowerShellColorizer.GetColorSpans(await AnalyzeAsync("<r a='v'/>", "a.xml"), theme));
    }

    [Fact]
    public async Task ExpandableStringColorOverlaysPutVariableAfterItsContainingString()
    {
        var theme = new EditorTheme();
        theme.Colors["Script.String"] = "#112233";
        theme.Colors["Script.Variable"] = "#445566";
        Assert.Equal(new SyntaxColorSpan[]
        {
            new(0, 9, Color.Parse("#112233")), new(3, 8, Color.Parse("#445566"))
        }, PowerShellColorizer.GetColorSpans(await AnalyzeAsync("\"x $name\""), theme));
    }

    [Theory]
    [InlineData(1, 1, 0, 6)]
    [InlineData(1, 9, 8, 14)]
    [InlineData(1, 2, -1, -1)]
    [InlineData(1, 7, -1, -1)]
    [InlineData(1, 15, -1, -1)]
    [InlineData(2, 1, -1, -1)]
    public async Task PausedStatementExtentDoesNotHighlightSameLineAdjacentText(int line, int column, int start, int end)
    {
        var path = Path.Combine(Path.GetTempPath(), "DtOwnedPaused.ps1");
        const string text = "$x = 1; $y = 2";
        var analysis = await AnalyzeAsync(text, path);
        var model = new ScriptTab(ScriptFile.FromBytes(path, Encoding.UTF8.GetBytes(text)));
        SetScriptAnalysis(model, analysis);
        var result = ScriptAdornments.PausedStatementSpan(model, new(path, line, column, "paused"));
        if (start < 0) Assert.Null(result);
        else Assert.Equal((start, end), result);
        Assert.Null(ScriptAdornments.PausedStatementSpan(model, null));
        Assert.Null(ScriptAdornments.PausedStatementSpan(model, new(path + ".other", 1, 1, "stale file")));
        model.Document.Insert(0, " ");
        Assert.Null(ScriptAdornments.PausedStatementSpan(model, new(path, line, column, "stale text")));
    }

    [Fact]
    public void PausedStatementExtentRejectsXmlDocuments()
    {
        var path = Path.Combine(Path.GetTempPath(), "DtOwnedPaused.xml");
        var model = new ScriptTab(ScriptFile.FromBytes(path, Encoding.UTF8.GetBytes("<r/>")));
        Assert.Null(ScriptAdornments.PausedStatementSpan(model, new(path, 1, 1, "paused")));
    }

    [Theory]
    [InlineData("", 137, 169)]
    [InlineData("<pre>A</pre>", 149, 181)]
    [InlineData("<pre>café\n  β😀</pre>", 162, 194)]
    public void WindowsClipboardHeaderPinsUtf8ByteOffsetsAndExactFragment(string fragment, int endFragment, int endHtml)
    {
        var actual = WorkbenchControl.CreateWindowsHtmlClipboard(fragment);
        var expectedHeader = "Version:1.0\r\nStartHTML:0000000105\r\nEndHTML:" + endHtml.ToString("D10") +
            "\r\nStartFragment:0000000137\r\nEndFragment:" + endFragment.ToString("D10") + "\r\n";
        var expected = expectedHeader + "<html><body><!--StartFragment-->" + fragment + "<!--EndFragment--></body></html>";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), actual);
        Assert.Equal(endHtml, actual.Length);
        Assert.Equal(fragment, Encoding.UTF8.GetString(actual, 137, endFragment - 137));
        Assert.Equal("<html><body><!--StartFragment-->", Encoding.UTF8.GetString(actual, 105, 32));
        Assert.Equal("<!--EndFragment--></body></html>", Encoding.UTF8.GetString(actual, endFragment, 32));
    }

    [AvaloniaFact]
    public void RichCopyEmptySelectionKeepsOnlyScriptPaletteWrapperAndValidatesBounds()
    {
        var theme = new EditorTheme();
        theme.Colors["Script.Foreground"] = "#112233";
        theme.Colors["Script.Background"] = "#445566";
        theme.Colors["Console.Background"] = "#778899";
        var analysis = new ScriptAnalysis([], [], []);
        Assert.Equal("<pre style=\"white-space:pre;tab-size:3;font-family:monospace;color:#112233;background-color:#445566\"></pre>",
            WorkbenchControl.CreateRichCopyHtml("not selected", analysis, new(4, 0), theme, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkbenchControl.CreateRichCopyHtml("abc", analysis, new(3, 1), theme));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkbenchControl.CreateRichCopyHtml("abc", analysis, new(0, 3), theme, 0));
    }

    [AvaloniaTheory]
    [InlineData("<&>\"'café😀")]
    [InlineData("<script>alert('x&y')</script>")]
    public void RichCopyEscapesMarkupAndPreservesOnlySelectedUnicodeText(string selected)
    {
        var theme = new EditorTheme();
        theme.Colors["Script.Foreground"] = "#112233";
        theme.Colors["Script.Background"] = "#445566";
        var html = WorkbenchControl.CreateRichCopyHtml(
            "before" + selected + "after", new ScriptAnalysis([], [], []), new(6, selected.Length), theme, 3);

        var root = XElement.Parse(html, LoadOptions.PreserveWhitespace);
        Assert.Equal("pre", root.Name.LocalName);
        Assert.Equal(selected, root.Value);
        Assert.Equal("white-space:pre;tab-size:3;font-family:monospace;color:#112233;background-color:#445566",
            root.Attribute("style")!.Value);
        Assert.Empty(root.Elements());
        Assert.Contains("&lt;", html);
        Assert.Contains("&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("before", html);
        Assert.DoesNotContain("after", html);
        Assert.DoesNotContain("<script>", html);
    }

    [AvaloniaTheory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void RichCopyPreservesExactSelectedLineDelimiters(string selected)
    {
        var html = WorkbenchControl.CreateRichCopyHtml(
            "before" + selected + "after", new ScriptAnalysis([], [], []), new(6, selected.Length), new EditorTheme());

        Assert.Contains(">" + selected + "</pre>", html);
        Assert.Equal("a\nb", XElement.Parse(html, LoadOptions.PreserveWhitespace).Value);
        Assert.DoesNotContain("before", html);
        Assert.DoesNotContain("after", html);
    }

    [AvaloniaFact]
    public async Task RichCopyClipsTokenColorToSelectedScriptTextNotConsolePalette()
    {
        var text = "before $x after";
        var theme = new EditorTheme();
        theme.Colors["Script.Variable"] = "#33AA55";
        theme.Colors["Console.Variable"] = "#AA3355";
        var html = WorkbenchControl.CreateRichCopyHtml(text, await AnalyzeAsync(text), new(7, 2), theme);

        var root = XElement.Parse(html, LoadOptions.PreserveWhitespace);
        var colored = Assert.Single(root.Elements("span"));
        Assert.Equal("$x", root.Value);
        Assert.Equal("$x", colored.Value);
        Assert.Contains("#33aa55", colored.Attribute("style")!.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#aa3355", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("before", html);
        Assert.DoesNotContain("after", html);
    }

    [AvaloniaFact]
    public async Task AnalysisNewRevisionReleasedFirstCannotBeOverwrittenByOlderDiagnostics()
    {
        var document = new TextDocument("old");
        using var editor = new PowerShellEditorControl(document);
        var first = new HeldAnalysis();
        editor.AnalysisProvider = first;
        var oldTask = editor.AnalyzeAsync();
        var oldRequest = await first.Request.Task;
        document.Text = "new text";
        var second = new HeldAnalysis();
        editor.AnalysisProvider = second;
        var newTask = editor.AnalyzeAsync();
        var newRequest = await second.Request.Task;
        Assert.Equal("new text", newRequest.Text);
        Assert.True(newRequest.Version > oldRequest.Version);
        second.Result.SetResult(new(newRequest.Version, EditorAnalysisState.Available,
            [new("New", "current diagnostic", EditorDiagnosticSeverity.Information, new(4, 4))]));
        var latest = await newTask;
        Assert.Equal(new EditorDiagnostic("New", "current diagnostic", EditorDiagnosticSeverity.Information, new(4, 4)),
            Assert.Single(editor.Analysis.Diagnostics));
        first.Result.SetResult(new(oldRequest.Version, EditorAnalysisState.Available,
            [new("Old", "obsolete diagnostic", EditorDiagnosticSeverity.Error, new(0, 3))]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldTask);
        Assert.Same(latest, editor.Analysis);
        Assert.Equal("new text", document.Text);
        Assert.Equal(newRequest.Version, editor.Analysis.Version);
    }

    [AvaloniaFact]
    public async Task AnalysisDetachRejectsThePendingResultAndClearsItsDiagnostics()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        var owner = new Window { Width = 600, Height = 400, Content = editor };
        try
        {
            owner.Show();
            var provider = new HeldAnalysis();
            editor.AnalysisProvider = provider;
            var pending = editor.AnalyzeAsync();
            var request = await provider.Request.Task;
            owner.Content = null;
            provider.Result.SetResult(new(request.Version, EditorAnalysisState.Available,
                [new("Old", "detached diagnostic", EditorDiagnosticSeverity.Error, new(0, 3))]));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(EditorAnalysisState.Unavailable, editor.Analysis.State);
            Assert.Empty(editor.Analysis.Diagnostics);
            Assert.Equal("abc", editor.Document.Text);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AnalysisAndCompletionRejectNullProviderResultsWithoutEditingOrPublishing()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.AnalysisProvider = new AnalysisFunction(_ => null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => editor.AnalyzeAsync());
        Assert.Equal(EditorAnalysisState.Failed, editor.Analysis.State);
        Assert.Empty(editor.Analysis.Diagnostics);
        editor.CompletionProvider = new CompletionFunction(_ => null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => editor.RequestCompletionAsync());
        Assert.Equal("abc", editor.Document.Text);
        Assert.Equal(0, editor.CaretOffset);
        Assert.False(editor.IsCompletionOpen);
    }

    [AvaloniaFact]
    public void SyntaxHighlightingDisableRemovesTheDefinitionWithoutChangingDocumentOrCaret()
    {
        var document = new TextDocument("$x = 'café'");
        using var editor = new PowerShellEditorControl(document);
        editor.CaretOffset = 4;
        Assert.Equal("PowerShell", editor.TextEditor.SyntaxHighlighting!.Name);
        editor.EnableSyntaxHighlighting = false;
        Assert.Null(editor.TextEditor.SyntaxHighlighting);
        Assert.Equal("$x = 'café'", document.Text);
        Assert.Equal(4, editor.CaretOffset);
        editor.EnableSyntaxHighlighting = true;
        Assert.Equal("PowerShell", editor.TextEditor.SyntaxHighlighting!.Name);
        Assert.Equal("$x = 'café'", document.Text);
        Assert.Equal(4, editor.CaretOffset);
    }

    [Theory]
    [InlineData("function Owned {}", "Function", 9, 14)]
    [InlineData("filter Owned {}", "Function", 7, 12)]
    [InlineData("Write-Output value", "Command", 0, 12)]
    [InlineData("Write-Output value", "CommandArgument", 13, 18)]
    [InlineData("Write-Output -Name", "Parameter", 13, 18)]
    [InlineData(":loop while ($true) {}", "Label", 0, 5)]
    [InlineData("if ($true) {}", "Keyword", 0, 2)]
    [InlineData("[string]$x", "Type", 1, 7)]
    [InlineData("$x.Length", "Member", 3, 9)]
    [InlineData("[Obsolete()]param()", "Attribute", 1, 9)]
    [InlineData("Write-Output @args", "Variable", 13, 18)]
    [InlineData("1 + 2", "Operator", 2, 3)]
    public async Task TokenCategoryColorsAndOffsetsHonorTheRequestedScriptOrConsolePane(
        string text, string category, int start, int end)
    {
        var analysis = await AnalyzeAsync(text);
        Assert.Empty(analysis.Errors);
        var theme = new EditorTheme();
        theme.Colors["Script." + category] = "#13579B";
        theme.Colors["Console." + category] = "#2468AC";
        Assert.Contains(new SyntaxColorSpan(start, end, Color.Parse("#13579B")),
            PowerShellColorizer.GetColorSpans(analysis, theme, "Script"));
        Assert.Contains(new SyntaxColorSpan(start, end, Color.Parse("#2468AC")),
            PowerShellColorizer.GetColorSpans(analysis, theme, "Console"));
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public void ApplyCompletionRejectsInvalidItemIndicesWithoutEditingOrMovingCaret(int index)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.CaretOffset = 2;
        var completion = new EditorCompletionList(editor.DocumentVersion, new(0, 1), [new("X", "X")]);
        Assert.Equal("itemIndex", Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplyCompletion(completion, index)).ParamName);
        Assert.Equal("abc", editor.Document.Text);
        Assert.Equal(2, editor.CaretOffset);
    }

    [AvaloniaFact]
    public void ApplyCompletionValidatesNullAndUninitializedItemsWithoutMutation()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        var version = editor.DocumentVersion;
        Assert.Throws<ArgumentNullException>(() => editor.ApplyCompletion(null!, 0));
        Assert.Throws<ArgumentException>(() => editor.ApplyCompletion(new(version, new(0, 1), default), 0));
        Assert.Throws<ArgumentException>(() => editor.ApplyCompletion(new(version, new(0, 1), [null!]), 0));
        Assert.Throws<ArgumentNullException>(() => editor.ApplyCompletion(new(version, new(0, 1), [new(null!, "X")]), 0));
        Assert.Equal("abc", editor.Document.Text);
        Assert.Equal(version, editor.DocumentVersion);
        Assert.Equal(0, editor.CaretOffset);
    }

    [AvaloniaTheory]
    [InlineData(0, 0, "Xabc", 1)]
    [InlineData(3, 0, "abcX", 4)]
    [InlineData(0, 3, "X", 1)]
    public void ApplyCompletionHandlesEmptyBeginningEndAndWholeDocumentSpans(
        int start, int length, string expected, int caret)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        editor.ApplyCompletion(new(editor.DocumentVersion, new(start, length), [new("X", "X")]), 0);
        Assert.Equal(expected, editor.Document.Text);
        Assert.Equal(caret, editor.CaretOffset);
    }

    private sealed class HeldAnalysis : IEditorAnalysisProvider
    {
        public TaskCompletionSource<EditorAnalysisRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EditorAnalysisResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken cancellationToken)
        {
            Request.SetResult(request);
            return Result.Task;
        }
    }

    private sealed class HeldCompletion : IEditorCompletionProvider
    {
        public TaskCompletionSource<EditorCompletionRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EditorCompletionList> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken)
        {
            Request.SetResult(request);
            return Result.Task;
        }
    }

    private sealed class AnalysisFunction(Func<EditorAnalysisRequest, EditorAnalysisResult> analyze) : IEditorAnalysisProvider
    {
        public Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(analyze(request));
    }

    private sealed class CompletionFunction(Func<EditorCompletionRequest, EditorCompletionList> complete) : IEditorCompletionProvider
    {
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(complete(request));
    }

    private sealed class ProtectedSection(bool denyInsertion) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => !denyInsertion;
        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment) =>
            denyInsertion ? new[] { segment } : new ISegment[] { new SimpleSegment(segment.Offset, segment.Length - 1) };
    }

    [AvaloniaFact]
    public void ApplyCompletionUsesTheSelectedSecondItemRatherThanTheFirst()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("ab"));
        editor.CaretOffset = 2;
        var version = editor.DocumentVersion;
        var completion = new EditorCompletionList(version, new(0, 2),
            [new("alpha", "Alpha", "first"), new("beta", "Beta", "second")]);

        editor.ApplyCompletion(completion, 1);

        Assert.Equal("beta", editor.Document.Text);
        Assert.Equal(4, editor.CaretOffset);
        Assert.True(editor.DocumentVersion > version);
    }

    [AvaloniaTheory]
    [InlineData(1, 5, "name;", "name", ";")]
    [InlineData(0, 3, "$na", "$na", "")]
    public async Task RichCopyClipsPartialVariableTokensWithoutColoringAdjacentOrdinaryText(
        int start, int length, string selected, string coloredText, string trailing)
    {
        const string text = "$name;";
        var theme = new EditorTheme();
        theme.Colors["Script.Variable"] = "#33AA55";
        theme.Colors["Script.Foreground"] = "#112233";
        theme.Colors["Script.Background"] = "#445566";
        var html = WorkbenchControl.CreateRichCopyHtml(text, await AnalyzeAsync(text), new(start, length), theme);

        var root = XElement.Parse(html, LoadOptions.PreserveWhitespace);
        var colored = Assert.Single(root.Elements("span"));
        Assert.Equal(selected, root.Value);
        Assert.Equal(coloredText, colored.Value);
        Assert.Equal("color:#33AA55;", colored.Attribute("style")!.Value, ignoreCase: true);
        Assert.Equal(trailing, string.Concat(colored.NodesAfterSelf().OfType<XText>().Select(node => node.Value)));
        Assert.Equal("white-space:pre;tab-size:4;font-family:monospace;color:#112233;background-color:#445566",
            root.Attribute("style")!.Value);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedTokenSegmentsUseCurrentPaletteAndHighContrastSuppressesOverlays(bool highContrast)
    {
        var previous = DesktopTheme.HighContrast;
        using var editor = new PowerShellEditorControl(new TextDocument("$old = 42")) { EnableSyntaxHighlighting = false };
        var theme = new EditorTheme();
        theme.Colors["Script.Variable"] = "#13579B";
        theme.Colors["Script.Number"] = "#2468AC";
        var colorizer = new PowerShellColorizer { Theme = theme, Analysis = await AnalyzeAsync("$old = 42") };
        editor.TextEditor.TextArea.TextView.LineTransformers.Add(colorizer);
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            DesktopTheme.Refresh(highContrast: false);
            owner.Show();
            Assert.Equal(Color.Parse("#13579B"), RenderedColorAt(editor.TextEditor, 0));
            Assert.Equal(Color.Parse("#2468AC"), RenderedColorAt(editor.TextEditor, 7));
            editor.Document.Text = "$new = 17";
            theme.Colors["Script.Variable"] = "#A1B2C3";
            colorizer.Theme = theme;
            colorizer.Analysis = await AnalyzeAsync("$new = 17");
            DesktopTheme.Refresh(highContrast: highContrast);
            editor.TextEditor.TextArea.TextView.Redraw();
            if (highContrast)
            {
                Assert.NotEqual(Color.Parse("#A1B2C3"), RenderedColorAt(editor.TextEditor, 0));
                Assert.NotEqual(Color.Parse("#2468AC"), RenderedColorAt(editor.TextEditor, 7));
            }
            else Assert.Equal(Color.Parse("#A1B2C3"), RenderedColorAt(editor.TextEditor, 0));
            DesktopTheme.Refresh(highContrast: false);
            editor.TextEditor.TextArea.TextView.Redraw();
            Assert.Equal(Color.Parse("#A1B2C3"), RenderedColorAt(editor.TextEditor, 0));
            Assert.Equal(Color.Parse("#2468AC"), RenderedColorAt(editor.TextEditor, 7));
            Assert.Equal("$new = 17", editor.Document.Text);
        }
        finally { owner.Content = null; owner.Close(); DesktopTheme.Refresh(highContrast: previous); }
    }

    [AvaloniaFact]
    public void DisabledSyntaxActuallyRemovesRenderedTokenColorAndReenableUsesEditedText()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("$old = 42"));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            owner.Show();
            var enabled = RenderedColorAt(editor.TextEditor, 0);
            editor.EnableSyntaxHighlighting = false;
            editor.Document.Text = "42 $new";
            editor.TextEditor.TextArea.TextView.Redraw();
            var ordinary = RenderedColorAt(editor.TextEditor, 3);
            Assert.NotEqual(enabled, ordinary);
            editor.EnableSyntaxHighlighting = true;
            editor.TextEditor.TextArea.TextView.Redraw();
            Assert.Equal(enabled, RenderedColorAt(editor.TextEditor, 3));
            Assert.NotEqual(ordinary, RenderedColorAt(editor.TextEditor, 0));
            Assert.Equal("42 $new", editor.Document.Text);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParserDiagnosticRendererDrawsOnlyExactErrorAndCorrectionClearsGeometry(bool highContrast)
    {
        var previous = DesktopTheme.HighContrast;
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            CreateInitialSession = true,
            Preferences = new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false, LoadProfiles = false }
        });
        var owner = new Window { Content = workbench, Width = 900, Height = 700 };
        try
        {
            DesktopTheme.Refresh(highContrast: highContrast);
            owner.Show();
            await workbench.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var editor = workbench.ScriptEditorView;
            editor.Document.Text = "Write-Output ok\n$x = )\nWrite-Output end";
            var analysis = await editor.AnalyzeAsync();
            Assert.Equal(new[] { ("ExpectedValueExpression", 20, 0), ("UnexpectedToken", 21, 1) },
                analysis.Diagnostics.Select(value => (value.Code, value.Span.Start, value.Span.Length)).ToArray());
            var diagnostic = Assert.Single(analysis.Diagnostics, value => value.Code == "UnexpectedToken");
            Assert.Equal("UnexpectedToken", diagnostic.Code);
            Assert.Equal(new EditorTextSpan(21, 1), diagnostic.Span);
            var view = editor.TextEditor.TextArea.TextView;
            owner.UpdateLayout();
            view.EnsureVisualLines();
            var renderer = Assert.Single(view.BackgroundRenderers.OfType<ScriptDiagnosticRenderer>());
            var errorRect = Assert.Single(AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(
                view, new SimpleSegment(21, 1)));
            var geometry = Assert.Single(CaptureDrawings(renderer, view),
                drawing => Math.Abs(drawing.Geometry!.Bounds.Left - errorRect.Left) < 0.000001);
            Assert.Equal(errorRect.Left, geometry.Geometry!.Bounds.Left, precision: 8);
            Assert.Equal(Math.Max(4, errorRect.Width), geometry.Geometry.Bounds.Width, precision: 8);
            Assert.InRange(geometry.Geometry.Bounds.Top, errorRect.Bottom - 3, errorRect.Bottom - 1);
            Assert.Equal(1, geometry.Pen!.Thickness);
            Assert.True(workbench.FindControl<TextBlock>("Diagnostics")!.IsVisible);
            Assert.Contains("Line 2:", workbench.FindControl<TextBlock>("Diagnostics")!.Text, StringComparison.Ordinal);
            editor.Document.Text = "Write-Output ok\n$x = 1\nWrite-Output end";
            var corrected = await editor.AnalyzeAsync();
            view.EnsureVisualLines();
            Assert.Empty(corrected.Diagnostics);
            Assert.Empty(CaptureDrawings(renderer, view));
            Assert.False(workbench.FindControl<TextBlock>("Diagnostics")!.IsVisible);
            Assert.Equal("Write-Output ok\n$x = 1\nWrite-Output end", editor.Document.Text);
        }
        finally
        {
            await workbench.DisposeAsync();
            owner.Content = null; owner.Close();
            DesktopTheme.Refresh(highContrast: previous);
        }
    }

    [AvaloniaTheory]
    [InlineData(EditorAnalysisState.Pending)]
    [InlineData(EditorAnalysisState.Unavailable)]
    [InlineData(EditorAnalysisState.Failed)]
    public void DiagnosticRendererDoesNotDrawUnavailablePendingFailedOrOutOfBoundsSegments(EditorAnalysisState state)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abc"));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            owner.Show();
            var view = editor.TextEditor.TextArea.TextView;
            view.EnsureVisualLines();
            var result = new EditorAnalysisResult(1, state,
                [new("E", "not available", EditorDiagnosticSeverity.Error, new(0, 2))]);
            var renderer = new ScriptDiagnosticRenderer(() => result, () => new EditorTheme());
            Assert.Empty(CaptureDrawings(renderer, view));
            result = new(1, EditorAnalysisState.Available,
                [new("E", "outside", EditorDiagnosticSeverity.Error, new(3, 1))]);
            Assert.Empty(CaptureDrawings(renderer, view));
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(EditorDiagnosticSeverity.Error, "#112233")]
    [InlineData(EditorDiagnosticSeverity.Warning, "#445566")]
    [InlineData(EditorDiagnosticSeverity.Information, "#445566")]
    public void DiagnosticRendererUsesSeverityPaletteWithoutCoveringAdjacentCharacters(
        EditorDiagnosticSeverity severity, string color)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("abcde"));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        var theme = new EditorTheme();
        theme.Colors["Stream.Error"] = "#112233";
        theme.Colors["Script.Parameter"] = "#445566";
        try
        {
            owner.Show();
            var view = editor.TextEditor.TextArea.TextView;
            view.EnsureVisualLines();
            var renderer = new ScriptDiagnosticRenderer(
                () => new(1, EditorAnalysisState.Available, [new("E", "literal", severity, new(1, 2))]), () => theme);
            var drawing = Assert.Single(CaptureDrawings(renderer, view));
            Assert.Equal(Color.Parse(color), Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Pen!.Brush).Color);
            var expected = Assert.Single(AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(view, new SimpleSegment(1, 2)));
            Assert.Equal(expected.Left, drawing.Geometry!.Bounds.Left, precision: 8);
            Assert.Equal(expected.Width, drawing.Geometry.Bounds.Width, precision: 8);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("$x = 1; $y = 2", 1, 9, 8, 6)]
    [InlineData("$x = @(\n  1,\n  2\n)\n$y = 3", 1, 1, 0, 18)]
    public async Task PausedStatementRendererDrawsOnlyExactSingleOrMultilineExtentAndInvalidatesEditedFile(
        string text, int line, int column, int start, int length)
    {
        var path = Path.Combine(Path.GetTempPath(), "DtOwnedRenderer.ps1");
        var analysis = await AnalyzeAsync(text, path);
        var model = new ScriptTab(ScriptFile.FromBytes(path, Encoding.UTF8.GetBytes(text)));
        SetScriptAnalysis(model, analysis);
        using var editor = new PowerShellEditorControl(model.Document);
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        DebugLocation? location = new(path, line, column, "paused");
        var renderer = new ScriptAdornments(() => model, () => location, () => new EditorTheme());
        try
        {
            owner.Show();
            var view = editor.TextEditor.TextArea.TextView;
            view.EnsureVisualLines();
            var expected = AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(view, new SimpleSegment(start, length)).ToArray();
            var drawings = CaptureDrawings(renderer, view);
            Assert.Equal(expected.Length, drawings.Length);
            Assert.Equal(expected, drawings.Select(drawing => drawing.Geometry!.Bounds).ToArray());
            Assert.All(drawings, drawing => Assert.Equal(65, Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color.A));
            location = null;
            Assert.Empty(CaptureDrawings(renderer, view));
            location = new(path, line, column, "paused again");
            Assert.Equal(expected.Length, CaptureDrawings(renderer, view).Length);
            model.Document.Insert(0, "#changed\n");
            view.EnsureVisualLines();
            Assert.Null(ScriptAdornments.PausedStatementSpan(model, location));
            // A stale location may retain the documented full-line fallback, but never the old statement rectangle.
            Assert.DoesNotContain(CaptureDrawings(renderer, view),
                drawing => Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color.A == 65);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaFact]
    public async Task LateAnalysisCannotRestoreClearedDiagnosticDrawing()
    {
        using var editor = new PowerShellEditorControl(new TextDocument("old"));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            owner.Show();
            var first = new HeldAnalysis();
            editor.AnalysisProvider = first;
            var pending = editor.AnalyzeAsync();
            var request = await first.Request.Task;
            editor.Document.Text = "corrected";
            editor.AnalysisProvider = new AnalysisFunction(r => new(r.Version, EditorAnalysisState.Available, []));
            await editor.AnalyzeAsync();
            var view = editor.TextEditor.TextArea.TextView;
            view.EnsureVisualLines();
            var renderer = new ScriptDiagnosticRenderer(() => editor.Analysis, () => new EditorTheme());
            Assert.Empty(CaptureDrawings(renderer, view));
            first.Result.SetResult(new(request.Version, EditorAnalysisState.Available,
                [new("Old", "must not draw", EditorDiagnosticSeverity.Error, new(0, 3))]));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Empty(CaptureDrawings(renderer, view));
            Assert.Equal(EditorAnalysisState.Available, editor.Analysis.State);
            Assert.Equal("corrected", editor.Document.Text);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    private static Color RenderedColorAt(AvaloniaEdit.TextEditor editor, int offset)
    {
        editor.UpdateLayout();
        var view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        var line = view.VisualLines.Single(line =>
            line.FirstDocumentLine.Offset <= offset && line.LastDocumentLine.EndOffset >= offset);
        var element = line.Elements.Single(element => line.FirstDocumentLine.Offset + element.RelativeTextOffset <= offset &&
            line.FirstDocumentLine.Offset + element.RelativeTextOffset + element.DocumentLength > offset);
        return Assert.IsAssignableFrom<ISolidColorBrush>(element.TextRunProperties.ForegroundBrush).Color;
    }

    private static GeometryDrawing[] CaptureDrawings(AvaloniaEdit.Rendering.IBackgroundRenderer renderer,
        AvaloniaEdit.Rendering.TextView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open()) renderer.Draw(view, context);
        return group.Children.Cast<GeometryDrawing>().ToArray();
    }

    [AvaloniaTheory]
    [InlineData("close")]
    [InlineData("detach")]
    [InlineData("dispose")]
    public async Task CompletionSelectedSecondDescriptionUpdatesAndLifetimeClosesBothPopups(string ending)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("ab"));
        editor.CompletionProvider = new CompletionFunction(request => new(request.Version, new(0, 2),
            [new("ab-alpha", "ab-alpha", "first description"), new("ab-beta", "ab-beta", "second description café")]));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            owner.Show();
            editor.CaretOffset = 2;
            Assert.True(editor.FocusEditor());
            await editor.ShowCompletionAsync();
            var popup = editor.CompletionPopup!;
            var description = Assert.Single(Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(popup)
                .OfType<Avalonia.Controls.Primitives.Popup>());
            popup.CompletionList.SelectedItem = popup.CompletionList.CompletionData[0];
            await PowerShellIseTestHarness.WaitForAsync(() => description.IsOpen, () => "First completion description did not open.");
            Assert.Equal("first description", Assert.IsAssignableFrom<ContentControl>(description.Child).Content);
            popup.CompletionList.SelectedItem = popup.CompletionList.CompletionData[1];
            await PowerShellIseTestHarness.WaitForAsync(
                () => Equals((description.Child as ContentControl)?.Content, "second description café"),
                () => "Selected second completion description did not replace the first.");
            Assert.Equal("ab-beta", popup.CompletionList.SelectedItem.Text);
            Assert.Equal(editor.TextEditor.TextArea.ActualThemeVariant, popup.CompletionList.ActualThemeVariant);
            Assert.Equal(editor.TextEditor.TextArea.ActualThemeVariant, description.Child!.ActualThemeVariant);
            if (ending == "close") editor.CloseCompletion();
            else if (ending == "detach") owner.Content = null;
            else editor.Dispose();
            Assert.False(popup.IsOpen);
            Assert.False(description.IsOpen);
            Assert.Null(Avalonia.LogicalTree.LogicalExtensions.GetLogicalParent(popup));
            Assert.Equal("ab", editor.Document.Text);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("analysis")]
    [InlineData("completion")]
    public async Task ExplicitProviderExceptionPropagatesOriginalFailureAndNeverMutatesDocument(string operation)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("kept"));
        editor.CaretOffset = 2;
        var failure = new InvalidOperationException("Owned provider failure.");
        if (operation == "analysis")
        {
            editor.AnalysisProvider = new AnalysisFunction(_ => throw failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => editor.AnalyzeAsync()));
            Assert.Equal(EditorAnalysisState.Failed, editor.Analysis.State);
            Assert.Empty(editor.Analysis.Diagnostics);
        }
        else
        {
            editor.CompletionProvider = new CompletionFunction(_ => throw failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => editor.RequestCompletionAsync()));
            Assert.False(editor.IsCompletionOpen);
        }
        Assert.Equal("kept", editor.Document.Text);
        Assert.Equal(2, editor.CaretOffset);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedEditorWithoutClipboardCannotPasteOrAlterOwnedClipboard(bool selection)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("before selected after"));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            owner.Show();
            editor.Select(selection ? new(7, 8) : new(4, 0));
            var caret = editor.CaretOffset;
            var span = editor.Selection;
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(owner.Clipboard!, "owned unavailable sentinel");
            owner.Content = null;
            Assert.Null(TopLevel.GetTopLevel(editor.TextEditor));
            // CanPaste reports edit permission; absence of a top-level is checked by the operation.
            Assert.True(editor.TextEditor.CanPaste);
            editor.TextEditor.Paste();
            Assert.Equal("before selected after", editor.Document.Text);
            Assert.Equal(caret, editor.CaretOffset);
            Assert.Equal(span, editor.Selection);
            Assert.Equal("owned unavailable sentinel", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("copy", false, false)]
    [InlineData("copy", false, true)]
    [InlineData("copy", true, false)]
    [InlineData("cut", false, false)]
    [InlineData("cut", false, true)]
    [InlineData("cut", true, false)]
    public async Task UnavailableClipboardCopyAndCutDoNotLoseDocumentOrOverwriteOwnedSentinel(
        string operation, bool selection, bool wholeLine)
    {
        using var editor = new PowerShellEditorControl(new TextDocument("before selected after"));
        var owner = new Window { Content = editor, Width = 650, Height = 350 };
        try
        {
            owner.Show();
            editor.TextEditor.Options.CutCopyWholeLine = wholeLine;
            editor.Select(selection ? new(7, 8) : new(4, 0));
            var caret = editor.CaretOffset;
            var span = editor.Selection;
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(owner.Clipboard!, "owned unavailable sentinel");
            owner.Content = null;
            Assert.Null(TopLevel.GetTopLevel(editor.TextEditor));
            if (operation == "copy") editor.TextEditor.Copy();
            else editor.TextEditor.Cut();
            Assert.Equal("owned unavailable sentinel", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
            Assert.Equal("before selected after", editor.Document.Text);
            Assert.Equal(caret, editor.CaretOffset);
            Assert.Equal(span, editor.Selection);
        }
        finally { owner.Content = null; owner.Close(); }
    }

    [AvaloniaFact]
    public async Task HighContrastSuppressesPausedAdornmentsButKeepsDiagnosticsInSystemForeground()
    {
        var previous = DesktopTheme.HighContrast;
        var path = Path.Combine(Path.GetTempPath(), "DtOwnedHighContrast-" + Guid.NewGuid().ToString("N") + ".ps1");
        const string text = "$x = 1; $y = 2";
        var analysis = await AnalyzeAsync(text, path);
        var model = new ScriptTab(ScriptFile.FromBytes(path, Encoding.UTF8.GetBytes(text)));
        SetScriptAnalysis(model, analysis);
        using var diagnosticEditor = new PowerShellEditorControl(new TextDocument("abcde")) { Height = 150 };
        using var pausedEditor = new PowerShellEditorControl(model.Document) { Height = 150 };
        var owner = new Window
        {
            Content = new StackPanel { Children = { diagnosticEditor, pausedEditor } },
            Width = 650,
            Height = 350
        };
        var theme = new EditorTheme();
        var diagnosticRenderer = new ScriptDiagnosticRenderer(
            () => new(1, EditorAnalysisState.Available,
                [new("Owned", "literal error", EditorDiagnosticSeverity.Error, new(1, 2))]), () => theme);
        var location = new DebugLocation(path, 1, 9, "paused");
        var pausedRenderer = new ScriptAdornments(() => model, () => location, () => theme);
        try
        {
            DesktopTheme.Refresh(highContrast: false);
            var ordinarySystemForeground = Assert.IsAssignableFrom<ISolidColorBrush>(
                DesktopTheme.Brush("WindowTextBrush")).Color;
            var authoredError = ordinarySystemForeground == Color.Parse("#13579B") ? "#2468AC" : "#13579B";
            theme.Colors["Stream.Error"] = authoredError;
            Assert.NotEqual(ordinarySystemForeground, Color.Parse(authoredError));
            owner.Show();
            owner.UpdateLayout();
            var diagnosticView = diagnosticEditor.TextEditor.TextArea.TextView;
            var pausedView = pausedEditor.TextEditor.TextArea.TextView;
            diagnosticView.EnsureVisualLines();
            pausedView.EnsureVisualLines();
            Assert.True(diagnosticView.VisualLinesValid);
            Assert.True(pausedView.VisualLinesValid);
            Assert.False(model.File.IsDirty);
            Assert.Equal((8, 14), ScriptAdornments.PausedStatementSpan(model, location)!.Value);
            var ordinaryDiagnostic = Assert.Single(CaptureDrawings(diagnosticRenderer, diagnosticView));
            Assert.Equal(Color.Parse(authoredError),
                Assert.IsAssignableFrom<ISolidColorBrush>(ordinaryDiagnostic.Pen!.Brush).Color);
            var diagnosticRect = Assert.Single(AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(
                diagnosticView, new SimpleSegment(1, 2)));
            Assert.Equal(diagnosticRect.Left, ordinaryDiagnostic.Geometry!.Bounds.Left, precision: 8);
            Assert.Equal(Math.Max(4, diagnosticRect.Width), ordinaryDiagnostic.Geometry.Bounds.Width, precision: 8);
            var ordinaryPaused = Assert.Single(CaptureDrawings(pausedRenderer, pausedView));
            var pausedRect = Assert.Single(AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(
                pausedView, new SimpleSegment(8, 6)));
            Assert.Equal(pausedRect, ordinaryPaused.Geometry!.Bounds);
            Assert.Equal(65, Assert.IsAssignableFrom<ISolidColorBrush>(ordinaryPaused.Brush).Color.A);

            DesktopTheme.Refresh(highContrast: true);
            var highContrastSystemForeground = Assert.IsAssignableFrom<ISolidColorBrush>(
                DesktopTheme.Brush("WindowTextBrush")).Color;
            Assert.NotEqual(Color.Parse(authoredError), highContrastSystemForeground);
            owner.UpdateLayout();
            diagnosticView.EnsureVisualLines();
            pausedView.EnsureVisualLines();
            Assert.True(diagnosticView.VisualLinesValid);
            Assert.True(pausedView.VisualLinesValid);
            Assert.Empty(CaptureDrawings(pausedRenderer, pausedView));
            var highContrastDiagnostic = Assert.Single(CaptureDrawings(diagnosticRenderer, diagnosticView));
            Assert.Equal(highContrastSystemForeground,
                Assert.IsAssignableFrom<ISolidColorBrush>(highContrastDiagnostic.Pen!.Brush).Color);
            Assert.Equal(1, highContrastDiagnostic.Pen.Thickness);
            Assert.Equal(diagnosticRect.Left, highContrastDiagnostic.Geometry!.Bounds.Left, precision: 8);
            Assert.Equal(Math.Max(4, diagnosticRect.Width), highContrastDiagnostic.Geometry.Bounds.Width, precision: 8);
            Assert.False(model.File.IsDirty);
            Assert.Equal("$x = 1; $y = 2", model.Document.Text);
            Assert.Equal("abcde", diagnosticEditor.Document.Text);
        }
        finally
        {
            try { owner.Content = null; owner.Close(); }
            finally { DesktopTheme.Refresh(highContrast: previous); }
        }
    }
}
