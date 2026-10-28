using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoOk.Services;

/// <summary>
/// Windows 셸(탐색기)이 만드는 미리보기를 쓴다. 셸 개체는 STA 스레드에서만 다루므로
/// 전용 STA 작업 스레드 몇 개가 요청을 차례로 처리한다. 파일은 셸이 읽기만 한다.
/// 미리보기를 만들 수 없으면 아이콘을 쓰고, 그것도 안 되면 null.
/// <see cref="Dispose"/>하면 새 요청을 받지 않고, 남은 요청은 만들지 않고 null로 끝낸 뒤 작업 스레드를 멈춘다.
/// </summary>
public sealed class ShellThumbnailProvider : IThumbnailProvider, IDisposable
{
    /// <summary>앱을 닫을 때 작업 스레드들이 지금 그림을 마치기를 기다리는 최대 시간(모든 스레드를 합쳐서).</summary>
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

    private readonly BlockingCollection<Request> _queue = new();
    private readonly List<Thread> _workers = [];
    private readonly TimeSpan _stopTimeout;
    private readonly Func<string, int, BitmapSource?> _create;
    private volatile bool _disposed;

    public ShellThumbnailProvider(int workerCount = 2)
        : this(workerCount, DefaultStopTimeout, Create)
    {
    }

    /// <param name="stopTimeout">닫을 때 모든 작업 스레드를 합쳐 기다리는 최대 시간.</param>
    /// <param name="create">그림을 만드는 함수(시험에서 셸 대신 쓴다).</param>
    internal ShellThumbnailProvider(int workerCount, TimeSpan stopTimeout, Func<string, int, BitmapSource?> create)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _stopTimeout = stopTimeout;
        _create = create;
        for (var i = 0; i < workerCount; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Name = $"PhotoOk 미리보기 {i + 1}" };
            thread.SetApartmentState(ApartmentState.STA);
            _workers.Add(thread);
            thread.Start();
        }
    }

    /// <summary>작업 스레드가 모두 멈췄으면 true(<see cref="Dispose"/> 뒤에만).</summary>
    public bool IsStopped => _disposed && _workers.All(w => !w.IsAlive);

    public Task<ImageSource?> GetAsync(string path, int size, CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested || string.IsNullOrEmpty(path) || size <= 0)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        var request = new Request(path, size, ct, new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            _queue.Add(request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 그사이 닫혔다.
            return Task.FromResult<ImageSource?>(null);
        }

        return request.Result.Task;
    }

    /// <summary>
    /// 새 요청을 받지 않고, 남은 요청은 null로 끝내고, 작업 스레드가 멈추기를 잠시 기다린다. 기다리는 시간은 스레드마다가 아니라
    /// 모두 합쳐 한 번의 마감(2초)이다. 여러 번 불러도 된다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        var stopped = true;
        var waited = Stopwatch.StartNew();
        foreach (var worker in _workers)
        {
            var remaining = _stopTimeout - waited.Elapsed;
            stopped &= worker.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }

        // 아직 셸 호출 중인 스레드가 있으면 대기열을 그 스레드가 다 쓸 때까지 남겨 둔다(배경 스레드라 앱 종료를 막지 않는다).
        if (stopped)
        {
            _queue.Dispose();
        }
    }

    private void Work()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            ImageSource? image = null;
            if (!_disposed && !request.Ct.IsCancellationRequested)
            {
                try
                {
                    image = _create(request.Path, request.Size);
                }
                catch (Exception)
                {
                    // 셸이 만들 수 없는 파일(손상·지원하지 않는 형식)은 미리보기 없이 보여 준다.
                    image = null;
                }
            }

            request.Result.TrySetResult(_disposed || request.Ct.IsCancellationRequested ? null : image);
        }
    }

    private static BitmapSource? Create(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr < 0 || factory is null)
        {
            return null;
        }

        try
        {
            var requested = new NativeSize { Width = size, Height = size };
            var hBitmap = IntPtr.Zero;
            hr = factory.GetImage(requested, ImageFlags.ThumbnailOnly, out hBitmap);
            if (hr < 0 || hBitmap == IntPtr.Zero)
            {
                hr = factory.GetImage(requested, ImageFlags.IconOnly, out hBitmap);
            }

            if (hr < 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                // GDI 비트맵 핸들(메모리 속 그림)을 돌려준다. 파일과는 관계없다.
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private sealed record Request(string Path, int Size, CancellationToken Ct, TaskCompletionSource<ImageSource?> Result);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [Flags]
    private enum ImageFlags
    {
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ImageFlags flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
