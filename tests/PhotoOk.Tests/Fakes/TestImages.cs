using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoOk.Tests.Fakes;

/// <summary>테스트용 JPEG을 메모리에서 직접 만든다(개인 사진은 쓰지 않는다).</summary>
public static class TestImages
{
    /// <summary>8x8 JPEG. 촬영 시각·제조사·모델을 주면 EXIF 메타데이터로 기록한다.</summary>
    public static byte[] Jpeg(DateTime? dateTaken = null, string? make = null, string? model = null)
    {
        byte[]? result = null;
        Exception? error = null;

        // WPF 이미징 객체는 스레드 소속이 있을 수 있어 전용 STA 스레드에서 만든다.
        var thread = new Thread(() =>
        {
            try
            {
                result = Encode(dateTaken, make, model);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            throw new InvalidOperationException("테스트 JPEG 생성에 실패했습니다.", error);
        }

        return result!;
    }

    private static byte[] Encode(DateTime? dateTaken, string? make, string? model)
    {
        var pixels = new byte[8 * 8 * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Rgb24, null, pixels, 8 * 3);

        BitmapMetadata? metadata = null;
        if (dateTaken is not null || make is not null || model is not null)
        {
            metadata = new BitmapMetadata("jpg");
            if (dateTaken is { } taken)
            {
                metadata.DateTaken = taken.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (make is not null)
            {
                metadata.CameraManufacturer = make;
            }

            if (model is not null)
            {
                metadata.CameraModel = model;
            }
        }

        var frame = BitmapFrame.Create(bitmap, null, metadata, null);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(frame);

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
