using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal static class ImageRenderDiagnosticsWindowScanner
{
    private static readonly MethodInfo? AttachMethod = typeof(ImageRenderDiagnostics).GetMethod(
        "OnMarkdownLoaded",
        BindingFlags.NonPublic | BindingFlags.Static);
    private static DispatcherTimer? _timer;
    private static int _started;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var dispatcher = app.Dispatcher;
        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.ContextIdle,
            (_, _) => Scan(),
            dispatcher);
        _timer.Start();
        dispatcher.BeginInvoke((Action)Scan, DispatcherPriority.ApplicationIdle);
    }

    private static void Scan()
    {
        var app = Application.Current;
        if (app == null || AttachMethod == null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            ScanNode(window);
        }
    }

    private static void ScanNode(DependencyObject node)
    {
        if (node is MarkdownTextBox box)
        {
            try
            {
                AttachMethod?.Invoke(
                    null,
                    new object[] { box, new RoutedEventArgs(FrameworkElement.LoadedEvent, box) });
            }
            catch
            {
            }
            return;
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            try
            {
                ScanNode(VisualTreeHelper.GetChild(node, i));
            }
            catch
            {
            }
        }
    }
}
