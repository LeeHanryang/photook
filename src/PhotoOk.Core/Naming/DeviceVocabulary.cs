using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;

namespace PhotoOk.Core.Naming;

/// <summary>
/// 이번 스캔의 사진에서 모은 기기 이름(제조사·모델)과 프리셋 별칭. 새 기기가 나와도 앱을 고치지 않고
/// 폴더 이름이 기기 이름인지 가려내는 데 쓴다. 비교는 공백·<c>-</c>·<c>_</c>와 대소문자를 무시한다.
/// </summary>
public sealed class DeviceVocabulary
{
    private const int MinLength = 2;

    private readonly HashSet<string> _names;

    private DeviceVocabulary(HashSet<string> names)
    {
        _names = names;
    }

    public static DeviceVocabulary Empty { get; } = new([]);

    /// <summary>
    /// 사진마다 제조사, 모델, 제조사+모델, 모델에서 제조사 접두를 뺀 값을 담고, 별칭의 이름과 모델도 담는다.
    /// 정규화한 길이가 2 미만인 값은 넣지 않는다.
    /// </summary>
    public static DeviceVocabulary From(IEnumerable<PhotoInfo> files, IEnumerable<DeviceAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(aliases);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(string?, string?)>();
        foreach (var file in files)
        {
            var make = file.CameraMake;
            var model = file.CameraModel;
            if (!seen.Add((make, model)))
            {
                continue; // 같은 기기의 사진이 수만 장이어도 정규화는 한 번만 한다.
            }

            Add(names, make);
            Add(names, model);
            if (!string.IsNullOrWhiteSpace(make) && !string.IsNullOrWhiteSpace(model))
            {
                Add(names, make + model);
                AddModelWithoutMake(names, make, model);
            }
        }

        foreach (var alias in aliases)
        {
            Add(names, alias.Alias);
            Add(names, alias.Model);
        }

        return new DeviceVocabulary(names);
    }

    /// <summary>폴더 이름이 어휘의 기기 이름과 같은가.</summary>
    public bool Contains(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        return _names.Contains(FolderNameNormalizer.Normalize(folderName));
    }

    // 모델이 "NIKON Z f"처럼 제조사로 시작하면 접두를 뗀 "Z f"도 담는다. 제조사가 "NIKON CORPORATION"이면 첫 단어로도 시도한다.
    private static void AddModelWithoutMake(HashSet<string> names, string make, string model)
    {
        var normalizedModel = FolderNameNormalizer.Normalize(model);
        var firstWord = make.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        foreach (var prefix in new[] { make, firstWord })
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                continue;
            }

            var normalizedPrefix = FolderNameNormalizer.Normalize(prefix);
            if (normalizedPrefix.Length > 0 && normalizedModel.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                AddNormalized(names, normalizedModel[normalizedPrefix.Length..]);
            }
        }
    }

    private static void Add(HashSet<string> names, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AddNormalized(names, FolderNameNormalizer.Normalize(value));
        }
    }

    private static void AddNormalized(HashSet<string> names, string normalized)
    {
        if (normalized.Length >= MinLength)
        {
            names.Add(normalized);
        }
    }
}
