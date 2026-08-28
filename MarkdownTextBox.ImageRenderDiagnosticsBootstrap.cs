namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    static MarkdownTextBox()
    {
        _ = ImageRenderDiagnostics.Initialize();
        ImageRenderDiagnosticsWindowScanner.Start();
    }
}
