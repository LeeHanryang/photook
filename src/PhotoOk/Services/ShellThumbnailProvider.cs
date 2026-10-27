using System.Collections.Concurrent;
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
/// </summary>
public sealed class ShellThumbnailProvider : IThumbnailProvider
{
    private readonly BlockingCollection<Request> _queue = new();

    public ShellThumbnailProvider(int workerCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        for (var i = 0; i < workerCount; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Name = $"PhotoOk 미리보기 {i + 1}" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    public Task<ImageSource?> GetAsync(string path, int size, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || string.IsNullOrEmpty(path) || size <= 0)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        var request = new Request(path, size, ct, new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Add(request, CancellationToken.None);
        return request.Result.Task;
    }

    private void Work()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            ImageSource? image = null;
            if (!request.Ct.IsCancellationRequested)
            {
                try
                {
                    image = Create(request.Path, request.Size);
                }
                catch (Exception)
                {
                    // 셸이 만들 수 없는 파일(손상·지원하지 않는 형식)은 미리보기 없이 보여 준다.
                    image = null;
                }
            }

            request.Result.TrySetResult(request.Ct.IsCancellationRequested ? null : image);
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
