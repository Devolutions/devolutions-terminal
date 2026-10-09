namespace Iseberg.Core;

public sealed class ShowCommandRequest
{
    public CommandFormDescription? Command { get; init; }
    public IReadOnlyList<CommandDescription> Commands { get; init; } = [];
    public string HelpText { get; init; } = "";
    public CommandHelpDocument? HelpDocument { get; init; }
    public Uri? HelpUri { get; init; }
    public bool PassThru { get; init; }
    public double Width { get; init; } = 360;
    public double Height { get; init; } = 410;
    public TaskCompletionSource<string?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class InputRequest(string caption, string message, bool secret = false)
{
    public string Caption { get; } = caption;
    public string Message { get; } = message;
    public bool Secret { get; } = secret;
    public IReadOnlyList<PromptChoice> Choices { get; init; } = [];
    public IReadOnlyList<int> DefaultChoices { get; init; } = [];
    public bool MultipleChoice { get; init; }
    public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class CommandErrorRequest(string message)
{
    public string Message { get; } = message;
    public TaskCompletionSource Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
