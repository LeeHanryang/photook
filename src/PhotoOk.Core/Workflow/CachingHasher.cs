using System.Collections.Concurrent;
using System.Globalization;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Workflow;

/// <summary>
/// 같은 파일을 다시 해시하지 않도록 결과를 기억한다. 키는 (전체 경로 - 대소문자 무시, 길이, 마지막 쓰기 시각)이라서
/// 파일이 바뀌면(크기나 수정 시각이 달라지면) 다시 계산한다. 해시에 실패하면 예외를 그대로 던지고 아무것도 기억하지 않는다.
/// </summary>
public sealed class CachingHasher(IHasher inner, IFileSystem fileSystem) : IHasher
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public string Hash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var entry = fileSystem.GetFile(path);
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFullPath(path).ToUpperInvariant()}|{entry.Length}|{entry.LastWriteTimeUtc.Ticks}");

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var hash = inner.Hash(path);
        _cache[key] = hash;
        return hash;
    }
}
