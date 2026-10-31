using System.Text;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Naming;

/// <summary>
/// 사용자가 "이 이름 다시 쓰지 않기"로 고른 폴더 이름. 줄마다 이름 하나인 글 파일(<c>ignored-folder-names.txt</c>)에서 읽고,
/// 이 파일에는 덧붙이기만 한다. 비교는 <see cref="DeviceVocabulary"/>와 같이 공백·<c>-</c>·<c>_</c>·대소문자를 무시하고,
/// 일반 단어 표처럼 끝의 숫자를 뗀 값도 본다.
/// </summary>
public sealed class IgnoredFolderNames
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HashSet<string> _names;

    private IgnoredFolderNames(HashSet<string> names)
    {
        _names = names;
    }

    public static IgnoredFolderNames Empty { get; } = new([]);

    /// <summary>파일이 없거나 읽을 수 없으면(<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) 빈 목록이다.</summary>
    public static IgnoredFolderNames Load(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        try
        {
            return From(ReadLines(fileSystem, path).Lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public bool Contains(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = FolderNameNormalizer.Normalize(name);
        return _names.Contains(normalized) || _names.Contains(FolderNameNormalizer.TrimTrailingDigits(normalized));
    }

    /// <summary>
    /// 이름을 한 줄 덧붙인다. 이미 같은 이름이 있으면 쓰지 않는다. 폴더가 없으면 만든다.
    /// 파일을 읽거나 쓰지 못하면 <see cref="IOException"/>·<see cref="UnauthorizedAccessException"/>이 그대로 나간다(부르는 쪽이 알린다).
    /// </summary>
    public static void Add(IFileSystem fileSystem, string path, string name)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(name);

        // 줄바꿈이 들어 있으면 여러 줄로 갈라지므로 공백으로 바꾼다.
        var line = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.Length == 0)
        {
            return;
        }

        var (lines, endsWithNewline) = ReadLines(fileSystem, path);
        if (From(lines).Contains(line))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            fileSystem.CreateDirectory(parent);
        }

        // 손으로 고친 파일이 줄바꿈 없이 끝나면 이어 붙지 않도록 먼저 줄을 끝낸다.
        fileSystem.AppendLines(path, endsWithNewline ? [line] : [string.Empty, line]);
    }

    private static IgnoredFolderNames From(IEnumerable<string> lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                var normalized = FolderNameNormalizer.Normalize(line);
                if (normalized.Length > 0)
                {
                    names.Add(normalized);
                }
            }
        }

        return new IgnoredFolderNames(names);
    }

    private static (List<string> Lines, bool EndsWithNewline) ReadLines(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.FileExists(path))
        {
            return ([], true);
        }

        string text;
        using (var stream = fileSystem.OpenReadShared(path))
        using (var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        return (lines, text.Length == 0 || text[^1] is '\r' or '\n');
    }
}
