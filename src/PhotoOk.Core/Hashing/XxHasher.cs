using System.Globalization;
using System.IO.Hashing;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Hashing;

/// <summary>xxHash64로 파일 내용을 해시한다. 파일은 읽기 전용으로만 연다.</summary>
public sealed class XxHasher(IFileSystem fileSystem) : IHasher
{
    public string Hash(string path)
    {
        var hash = new XxHash64();
        using (var stream = fileSystem.OpenRead(path))
        {
            hash.Append(stream);
        }

        return Format(hash.GetCurrentHashAsUInt64());
    }

    /// <summary>소문자 16자리 16진수.</summary>
    public static string Format(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
}
