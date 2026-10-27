namespace PhotoOk.Core.Metadata;

/// <summary>확장자(대소문자 무시)로 파일 종류를 분류한다.</summary>
public sealed class FileKinds
{
    private readonly Dictionary<string, MediaKind> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public static FileKinds Default { get; } = CreateDefault();

    private static FileKinds CreateDefault()
    {
        var kinds = new FileKinds();
        kinds.Add(MediaKind.Image, ".jpg", ".jpeg", ".heic", ".heif", ".png", ".tif", ".tiff", ".webp");
        kinds.Add(MediaKind.Raw, ".rw2", ".raf", ".nef", ".nrw", ".cr2", ".cr3", ".arw", ".dng", ".orf", ".pef", ".srw");
        kinds.Add(MediaKind.Video, ".mp4", ".mov", ".m4v", ".avi", ".mts", ".m2ts");
        kinds.Add(MediaKind.Sidecar, ".xmp", ".pp3", ".dop", ".aae");
        return kinds;
    }

    private void Add(MediaKind kind, params string[] extensions)
    {
        foreach (var extension in extensions)
        {
            _byExtension[extension] = kind;
        }
    }

    public MediaKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        return _byExtension.TryGetValue(extension, out var kind) ? kind : MediaKind.Other;
    }

    /// <summary>Image, Raw, Video이면 true. 사이드카와 기타 파일은 false.</summary>
    public bool IsMediaExtension(string extensionWithDot)
    {
        return _byExtension.TryGetValue(extensionWithDot, out var kind)
            && kind is MediaKind.Image or MediaKind.Raw or MediaKind.Video;
    }
}
