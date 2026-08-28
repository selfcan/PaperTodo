using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PaperTodo;

internal readonly record struct ImageRenderDiagnosticSnapshot(
    int BoxId,
    string NoteId,
    bool HasImages,
    bool PreviewMode,
    bool PostPasteQueued,
    bool ImageRedrawQueued,
    bool MarkdownSuffixQueued,
    bool ResizePreview,
    bool RenderingSuspended,
    bool ViewportPreviewQueued,
    bool ViewportProtectedQueued,
    bool VisualLinesValid,
    int VisualLineCount,
    int ImageBlockCount,
    int ImageElementIdentityXor,
    int BitmapSourceIdentityXor,
    int SourcePixelWidthMin,
    int SourcePixelWidthMax,
    double ActualWidth,
    double ActualHeight,
    double TextViewWidth,
    double TextViewHeight,
    double ScrollY,
    double DpiScaleX)
{
    public string Compact()
        => FormattableString.Invariant(
            $"box={BoxId} note={NoteId} hasImages={HasImages} preview={PreviewMode} postQ={PostPasteQueued} imageRedrawQ={ImageRedrawQueued} suffixQ={MarkdownSuffixQueued} resizePreview={ResizePreview} suspended={RenderingSuspended} viewportQ={ViewportPreviewQueued} protectQ={ViewportProtectedQueued} visualValid={VisualLinesValid} visualLines={VisualLineCount} imageBlocks={ImageBlockCount} imageXor={ImageElementIdentityXor} bitmapXor={BitmapSourceIdentityXor} srcPx={SourcePixelWidthMin}-{SourcePixelWidthMax} actual={ActualWidth:F2}x{ActualHeight:F2} textView={TextViewWidth:F2}x{TextViewHeight:F2} scrollY={ScrollY:F2} dpi={DpiScaleX:F3}");
}

internal static class ImageRenderDiagnostics
{
    private const string BuildLabel = "3.x";
    private const int Capacity = 1_000_000;
    private static readonly object Gate = new();
    private static readonly TraceEntry[] Entries = new TraceEntry[Capacity];
    private static readonly Dictionary<MarkdownTextBox, WatchState> Watches = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<int> SeenBitmapSources = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly System.Diagnostics.Process _process = System.Diagnostics.Process.GetCurrentProcess();
    private static int _initialized;
    private static int _writeIndex;
    private static int _count;
    private static long _sequence;
    private static long _overwritten;
    private static int _flushStarted;
    private static bool _runtimeHooksAttached;
    private static DispatcherTimer? _sampleTimer;
    private static TimeSpan _lastCpu = _process.TotalProcessorTime;
    private static long _lastSampleTicks = Stopwatch.GetTimestamp();
    private static long _renderFrames;
    private static long _visualLinesChanged;
    private static long _layoutUpdated;
    private static long _sizeChanged;
    private static long _textChanged;
    private static long _dispatcherPosted;
    private static long _dispatcherStarted;
    private static long _dispatcherCompleted;
    private static long _imageLoaded;
    private static long _imageUnloaded;
    private static long _newBitmapSources;
    private static int _visualStackSamples;
    private static int _layoutStackSamples;
    private static int _dispatcherStackSamples;
    private static int _sizeStackSamples;
    private static int _textStackSamples;
    private static int _imageStackSamples;

    internal static bool Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return true;
        }

        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMarkdownLoaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnMarkdownUnloaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(Image),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnImageLoaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(Image),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnImageUnloaded),
            true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush("ProcessExit");
        Record("DiagnosticsStart", "", $"build={BuildLabel} capacity={Capacity}");
        return true;
    }

    private static void OnMarkdownLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MarkdownTextBox box)
        {
            return;
        }

        lock (Gate)
        {
            if (Watches.ContainsKey(box))
            {
                return;
            }
            Watches.Add(box, new WatchState());
        }

        box.SizeChanged += OnSizeChanged;
        box.LayoutUpdated += OnLayoutUpdated;
        box.TextChanged += OnTextChanged;
        box.IsVisibleChanged += OnIsVisibleChanged;
        box.TextArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        EnsureRuntimeHooks(box.Dispatcher);
        RecordBox("Loaded", box, includeVisualTree: true, captureStack: true);
    }

    private static void OnMarkdownUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MarkdownTextBox box)
        {
            return;
        }

        RecordBox("Unloaded", box, includeVisualTree: true, captureStack: true);
        box.SizeChanged -= OnSizeChanged;
        box.LayoutUpdated -= OnLayoutUpdated;
        box.TextChanged -= OnTextChanged;
        box.IsVisibleChanged -= OnIsVisibleChanged;
        box.TextArea.TextView.VisualLinesChanged -= OnVisualLinesChanged;
        lock (Gate)
        {
            Watches.Remove(box);
        }
    }

    private static void OnImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image ||
            !MarkdownTextBox.TryDescribeDiagnosticImage(
                image,
                out var owner,
                out var imageId,
                out var sourceId,
                out var detail))
        {
            return;
        }

        Interlocked.Increment(ref _imageLoaded);
        var isNewSource = false;
        if (sourceId != 0)
        {
            lock (Gate)
            {
                isNewSource = SeenBitmapSources.Add(sourceId);
            }
            if (isNewSource)
            {
                Interlocked.Increment(ref _newBitmapSources);
            }
        }

        var capture = ShouldSampleStack(ref _imageStackSamples, 96);
        Record(
            "ImageLoaded",
            owner?.DiagnosticNoteId ?? "",
            $"imageId={imageId} newBitmapSource={isNewSource} {detail}",
            capture);
    }

    private static void OnImageUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image ||
            !MarkdownTextBox.TryDescribeDiagnosticImage(
                image,
                out var owner,
                out var imageId,
                out _,
                out var detail))
        {
            return;
        }

        Interlocked.Increment(ref _imageUnloaded);
        Record("ImageUnloaded", owner?.DiagnosticNoteId ?? "", $"imageId={imageId} {detail}");
    }

    private static void EnsureRuntimeHooks(Dispatcher dispatcher)
    {
        lock (Gate)
        {
            if (_runtimeHooksAttached)
            {
                return;
            }
            _runtimeHooksAttached = true;
        }

        CompositionTarget.Rendering += OnRendering;
        dispatcher.Hooks.OperationPosted += OnDispatcherPosted;
        dispatcher.Hooks.OperationStarted += OnDispatcherStarted;
        dispatcher.Hooks.OperationCompleted += OnDispatcherCompleted;
        InputManager.Current.PostProcessInput += OnPostProcessInput;
        _sampleTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => Sample(),
            dispatcher);
        _sampleTimer.Start();
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (!HasAnyActiveImageBox())
        {
            return;
        }
        Interlocked.Increment(ref _renderFrames);
        Record("RenderFrame", "", "");
    }

    private static void OnDispatcherPosted(object? sender, DispatcherHookEventArgs e)
    {
        if (!HasAnyActiveImageBox())
        {
            return;
        }
        Interlocked.Increment(ref _dispatcherPosted);
        var capture = ShouldSampleStack(ref _dispatcherStackSamples, 128);
        Record("DispatcherPosted", "", $"priority={e.Operation.Priority} status={e.Operation.Status}", capture);
    }

    private static void OnDispatcherStarted(object? sender, DispatcherHookEventArgs e)
    {
        if (!HasAnyActiveImageBox())
        {
            return;
        }
        Interlocked.Increment(ref _dispatcherStarted);
        Record("DispatcherStarted", "", $"priority={e.Operation.Priority} status={e.Operation.Status}");
    }

    private static void OnDispatcherCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!HasAnyActiveImageBox())
        {
            return;
        }
        Interlocked.Increment(ref _dispatcherCompleted);
        Record("DispatcherCompleted", "", $"priority={e.Operation.Priority} status={e.Operation.Status}");
    }

    private static void OnPostProcessInput(object sender, ProcessInputEventArgs e)
    {
        if (!HasAnyActiveImageBox())
        {
            return;
        }
        Record("Input", "", $"type={e.StagingItem.Input?.GetType().Name ?? "null"}");
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not MarkdownTextBox box)
        {
            return;
        }
        Interlocked.Increment(ref _sizeChanged);
        var capture = ShouldSampleStack(ref _sizeStackSamples, 64);
        RecordBox(
            "SizeChanged",
            box,
            includeVisualTree: true,
            captureStack: capture,
            prefix: FormattableString.Invariant(
                $"old={e.PreviousSize.Width:F2}x{e.PreviousSize.Height:F2} new={e.NewSize.Width:F2}x{e.NewSize.Height:F2}"));
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not MarkdownTextBox box || !box.DiagnosticHasImages)
        {
            return;
        }
        Interlocked.Increment(ref _layoutUpdated);
        var capture = ShouldSampleStack(ref _layoutStackSamples, 24);
        RecordBox("LayoutUpdated", box, includeVisualTree: false, captureStack: capture);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not MarkdownTextBox box)
        {
            return;
        }
        Interlocked.Increment(ref _textChanged);
        var capture = ShouldSampleStack(ref _textStackSamples, 32);
        RecordBox("TextChanged", box, includeVisualTree: false, captureStack: capture);
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is MarkdownTextBox box)
        {
            RecordBox(
                "IsVisibleChanged",
                box,
                includeVisualTree: false,
                captureStack: true,
                prefix: $"visible={e.NewValue}");
        }
    }

    private static void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        MarkdownTextBox? box = null;
        lock (Gate)
        {
            foreach (var candidate in Watches.Keys)
            {
                if (ReferenceEquals(candidate.TextArea.TextView, sender))
                {
                    box = candidate;
                    break;
                }
            }
        }

        if (box == null || !box.DiagnosticHasImages)
        {
            return;
        }

        Interlocked.Increment(ref _visualLinesChanged);
        var capture = ShouldSampleStack(ref _visualStackSamples, 96);
        RecordBox("VisualLinesChanged", box, includeVisualTree: true, captureStack: capture);
    }

    private static void RecordBox(
        string kind,
        MarkdownTextBox box,
        bool includeVisualTree,
        bool captureStack,
        string? prefix = null)
    {
        ImageRenderDiagnosticSnapshot snapshot;
        try
        {
            snapshot = box.CaptureImageRenderDiagnosticSnapshot(includeVisualTree);
        }
        catch (Exception ex)
        {
            Record(kind + "SnapshotFailed", "", ex.GetType().Name + ":" + ex.Message, captureStack);
            return;
        }

        UpdateWatch(box, snapshot);
        DetectStateTransition(box, snapshot);
        var detail = string.IsNullOrEmpty(prefix)
            ? snapshot.Compact()
            : prefix + " " + snapshot.Compact();
        Record(kind, snapshot.NoteId, detail, captureStack);
    }

    private static void UpdateWatch(MarkdownTextBox box, ImageRenderDiagnosticSnapshot snapshot)
    {
        lock (Gate)
        {
            if (Watches.TryGetValue(box, out var state))
            {
                state.HasImages = snapshot.HasImages;
                state.IsVisible = box.IsVisible;
            }
        }
    }

    private static void DetectStateTransition(MarkdownTextBox box, ImageRenderDiagnosticSnapshot current)
    {
        ImageRenderDiagnosticSnapshot previous = default;
        var hadPrevious = false;
        lock (Gate)
        {
            if (Watches.TryGetValue(box, out var state))
            {
                hadPrevious = state.HasSnapshot;
                previous = state.LastSnapshot;
                state.LastSnapshot = current;
                state.HasSnapshot = true;
            }
        }

        if (!hadPrevious)
        {
            return;
        }

        if (previous.PostPasteQueued != current.PostPasteQueued ||
            previous.ImageRedrawQueued != current.ImageRedrawQueued ||
            previous.MarkdownSuffixQueued != current.MarkdownSuffixQueued ||
            previous.ResizePreview != current.ResizePreview ||
            previous.RenderingSuspended != current.RenderingSuspended ||
            previous.ViewportPreviewQueued != current.ViewportPreviewQueued ||
            previous.ViewportProtectedQueued != current.ViewportProtectedQueued ||
            previous.VisualLinesValid != current.VisualLinesValid)
        {
            Record(
                "StateTransition",
                current.NoteId,
                "from=[" + previous.Compact() + "] to=[" + current.Compact() + "]",
                captureStack: true);
        }
    }

    private static bool HasAnyActiveImageBox()
    {
        lock (Gate)
        {
            foreach (var state in Watches.Values)
            {
                if (state.HasImages && state.IsVisible)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void Sample()
    {
        try
        {
            _process.Refresh();
            var nowTicks = Stopwatch.GetTimestamp();
            var cpu = _process.TotalProcessorTime;
            var elapsedMs = (nowTicks - _lastSampleTicks) * 1000.0 / Stopwatch.Frequency;
            var cpuMs = (cpu - _lastCpu).TotalMilliseconds;
            var cpuPercent = elapsedMs <= 0
                ? 0
                : cpuMs / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100.0;
            _lastSampleTicks = nowTicks;
            _lastCpu = cpu;

            var detail = FormattableString.Invariant(
                $"cpu={cpuPercent:F2}% workingMB={_process.WorkingSet64 / 1048576.0:F1} privateMB={_process.PrivateMemorySize64 / 1048576.0:F1} managedMB={GC.GetTotalMemory(false) / 1048576.0:F1} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} render={Interlocked.Exchange(ref _renderFrames, 0)} visual={Interlocked.Exchange(ref _visualLinesChanged, 0)} layout={Interlocked.Exchange(ref _layoutUpdated, 0)} size={Interlocked.Exchange(ref _sizeChanged, 0)} text={Interlocked.Exchange(ref _textChanged, 0)} imageLoad={Interlocked.Exchange(ref _imageLoaded, 0)} imageUnload={Interlocked.Exchange(ref _imageUnloaded, 0)} newBitmap={Interlocked.Exchange(ref _newBitmapSources, 0)} dispPosted={Interlocked.Exchange(ref _dispatcherPosted, 0)} dispStarted={Interlocked.Exchange(ref _dispatcherStarted, 0)} dispDone={Interlocked.Exchange(ref _dispatcherCompleted, 0)} watches={WatchCount()} seenBitmaps={SeenBitmapCount()}");
            Record("Sample1s", "", detail);

            foreach (var box in WatchedBoxes())
            {
                if (box.DiagnosticHasImages)
                {
                    RecordBox("SampleBox1s", box, includeVisualTree: true, captureStack: false);
                }
            }
        }
        catch (Exception ex)
        {
            Record("SampleFailed", "", ex.GetType().Name + ":" + ex.Message);
        }
    }

    private static int WatchCount()
    {
        lock (Gate)
        {
            return Watches.Count;
        }
    }

    private static int SeenBitmapCount()
    {
        lock (Gate)
        {
            return SeenBitmapSources.Count;
        }
    }

    private static MarkdownTextBox[] WatchedBoxes()
    {
        lock (Gate)
        {
            return Watches.Keys.ToArray();
        }
    }

    private static bool ShouldSampleStack(ref int counter, int initialLimit)
    {
        var n = Interlocked.Increment(ref counter);
        return n <= initialLimit || (n & (n - 1)) == 0 || n % 5000 == 0;
    }

    private static void Record(string kind, string? noteId, string? detail, bool captureStack = false)
    {
        var entry = new TraceEntry(
            Interlocked.Increment(ref _sequence),
            Clock.ElapsedTicks,
            Environment.CurrentManagedThreadId,
            kind,
            noteId ?? "",
            Sanitize(detail),
            captureStack ? CaptureStack() : null);

        lock (Gate)
        {
            Entries[_writeIndex] = entry;
            _writeIndex = (_writeIndex + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
            else
            {
                _overwritten++;
            }
        }
    }

    private static string? CaptureStack()
    {
        try
        {
            return new StackTrace(3, false).ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? ""
            : value.Replace('\r', ' ').Replace('\n', ' ');

    private static void Flush(string reason)
    {
        if (Interlocked.Exchange(ref _flushStarted, 1) != 0)
        {
            return;
        }

        TraceEntry[] snapshot;
        long overwritten;
        lock (Gate)
        {
            snapshot = new TraceEntry[_count];
            var start = _count == Capacity ? _writeIndex : 0;
            for (var i = 0; i < _count; i++)
            {
                snapshot[i] = Entries[(start + i) % Capacity];
            }
            overwritten = _overwritten;
        }

        var fileName = $"PaperTodo.image-render-{BuildLabel}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
        var primary = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!TryWrite(primary, snapshot, overwritten, reason))
        {
            _ = TryWrite(Path.Combine(Path.GetTempPath(), fileName), snapshot, overwritten, reason);
        }
    }

    private static bool TryWrite(string path, TraceEntry[] entries, long overwritten, string reason)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                1024 * 1024);
            using var writer = new StreamWriter(
                stream,
                new System.Text.UTF8Encoding(false),
                1024 * 1024);
            writer.WriteLine($"=== PaperTodo image render diagnostics build={BuildLabel} reason={reason} ===");
            writer.WriteLine($"started={DateTimeOffset.Now - Clock.Elapsed} duration={Clock.Elapsed} events={entries.Length} overwritten={overwritten} pid={Environment.ProcessId} cpuCount={Environment.ProcessorCount}");
            writer.WriteLine($"os={Environment.OSVersion} framework={Environment.Version}");

            foreach (var entry in entries)
            {
                var ms = entry.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
                writer.WriteLine(FormattableString.Invariant(
                    $"#{entry.Sequence} +{ms:F3}ms T{entry.ThreadId} {entry.Kind} note={entry.NoteId} {entry.Detail}"));
                if (!string.IsNullOrWhiteSpace(entry.Stack))
                {
                    writer.WriteLine("STACK_BEGIN");
                    writer.Write(entry.Stack);
                    if (!entry.Stack!.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    {
                        writer.WriteLine();
                    }
                    writer.WriteLine("STACK_END");
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class WatchState
    {
        public bool HasSnapshot { get; set; }
        public bool HasImages { get; set; }
        public bool IsVisible { get; set; }
        public ImageRenderDiagnosticSnapshot LastSnapshot { get; set; }
    }

    private readonly record struct TraceEntry(
        long Sequence,
        long ElapsedTicks,
        int ThreadId,
        string Kind,
        string NoteId,
        string Detail,
        string? Stack);
}

public sealed partial class MarkdownTextBox
{
    private static readonly bool ImageRenderDiagnosticsRegistered = ImageRenderDiagnostics.Initialize();

    internal bool DiagnosticHasImages => _hadInternalImageReferences;
    internal string DiagnosticNoteId => _noteId;

    internal ImageRenderDiagnosticSnapshot CaptureImageRenderDiagnosticSnapshot(bool includeVisualTree)
    {
        var textView = TextArea.TextView;
        var visualLinesValid = textView.VisualLinesValid;
        var visualLineCount = visualLinesValid ? textView.VisualLines.Count : -1;
        var imageBlockCount = 0;
        var imageElementIdentityXor = 0;
        var bitmapSourceIdentityXor = 0;
        var sourcePixelWidthMin = int.MaxValue;
        var sourcePixelWidthMax = 0;

        if (includeVisualTree && visualLinesValid)
        {
            CollectDiagnosticImageStats(
                textView,
                ref imageBlockCount,
                ref imageElementIdentityXor,
                ref bitmapSourceIdentityXor,
                ref sourcePixelWidthMin,
                ref sourcePixelWidthMax);
        }

        if (sourcePixelWidthMin == int.MaxValue)
        {
            sourcePixelWidthMin = 0;
        }

        return new ImageRenderDiagnosticSnapshot(
            RuntimeHelpers.GetHashCode(this),
            _noteId,
            _hadInternalImageReferences,
            _isPreviewMode,
            _isPostPasteRefreshQueued,
            _isImageRenderRedrawQueued,
            _isMarkdownSuffixRedrawQueued,
            _isImageResizePreview,
            _imageRenderingSuspended,
            _isImageViewportPreviewQueued,
            _isViewportProtectedRefreshQueued,
            visualLinesValid,
            visualLineCount,
            imageBlockCount,
            imageElementIdentityXor,
            bitmapSourceIdentityXor,
            sourcePixelWidthMin,
            sourcePixelWidthMax,
            ActualWidth,
            ActualHeight,
            textView.ActualWidth,
            textView.ActualHeight,
            textView.ScrollOffset.Y,
            VisualTreeHelper.GetDpi(textView).DpiScaleX);
    }

    internal static bool TryDescribeDiagnosticImage(
        Image image,
        out MarkdownTextBox? owner,
        out string imageId,
        out int sourceId,
        out string detail)
    {
        owner = null;
        imageId = "";
        sourceId = 0;
        detail = "";

        DependencyObject? parent;
        try
        {
            parent = VisualTreeHelper.GetParent(image);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (parent is not Border { Tag: ImageBlockTag tag } host)
        {
            return false;
        }

        var node = (DependencyObject?)host;
        while (node != null)
        {
            if (node is MarkdownTextBox box)
            {
                owner = box;
                break;
            }

            try
            {
                node = VisualTreeHelper.GetParent(node);
            }
            catch (InvalidOperationException)
            {
                node = null;
            }
        }

        imageId = tag.ImageId;
        var imageObjectId = RuntimeHelpers.GetHashCode(image);
        var sourcePixelWidth = 0;
        var sourcePixelHeight = 0;
        if (image.Source is BitmapSource bitmap)
        {
            sourceId = RuntimeHelpers.GetHashCode(bitmap);
            sourcePixelWidth = bitmap.PixelWidth;
            sourcePixelHeight = bitmap.PixelHeight;
        }

        detail = FormattableString.Invariant(
            $"imageObj={imageObjectId} sourceObj={sourceId} sourcePx={sourcePixelWidth}x{sourcePixelHeight} imageWidth={image.Width:F2} actualWidth={image.ActualWidth:F2} hostWidth={host.Width:F2}");
        return true;
    }

    private static void CollectDiagnosticImageStats(
        DependencyObject node,
        ref int count,
        ref int imageElementIdentityXor,
        ref int bitmapSourceIdentityXor,
        ref int minSourcePixelWidth,
        ref int maxSourcePixelWidth)
    {
        if (node is Border { Tag: ImageBlockTag } host)
        {
            count++;
            if (host.Child is Image image)
            {
                imageElementIdentityXor ^= RuntimeHelpers.GetHashCode(image);
                if (image.Source is BitmapSource bitmap)
                {
                    bitmapSourceIdentityXor ^= RuntimeHelpers.GetHashCode(bitmap);
                    minSourcePixelWidth = Math.Min(minSourcePixelWidth, bitmap.PixelWidth);
                    maxSourcePixelWidth = Math.Max(maxSourcePixelWidth, bitmap.PixelWidth);
                }
            }
            return;
        }

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        for (var i = 0; i < childCount; i++)
        {
            try
            {
                CollectDiagnosticImageStats(
                    VisualTreeHelper.GetChild(node, i),
                    ref count,
                    ref imageElementIdentityXor,
                    ref bitmapSourceIdentityXor,
                    ref minSourcePixelWidth,
                    ref maxSourcePixelWidth);
            }
            catch (InvalidOperationException)
            {
                // Visual lines can be replaced while the diagnostic walk is in progress.
            }
        }
    }
}
