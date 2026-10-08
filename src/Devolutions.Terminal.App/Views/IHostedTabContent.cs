namespace Devolutions.Terminal.App.Views;

public interface IHostedTabContent : IAsyncDisposable
{
    bool IsDisposed { get; }
    Task InitializeAsync();
    Task<bool> PrepareCloseAsync();
    void CancelClosePreparation();
    Task<bool> RequestCloseAsync();
    void FocusContent();
}

public interface IHostedTerminalContent : IHostedTabContent
{
    TermControl Terminal { get; }
    bool IsTerminalActive => Terminal.IsKeyboardFocusWithin;
}
