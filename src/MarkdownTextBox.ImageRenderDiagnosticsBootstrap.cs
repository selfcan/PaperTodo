namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    static MarkdownTextBox()
    {
        // Force image diagnostics to register before the first MarkdownTextBox instance is created.
        // Without an explicit type initializer, the runtime may defer the existing static field
        // initializer and miss Loaded/Unloaded events entirely.
        _ = ImageRenderDiagnostics.Initialize();
    }
}
