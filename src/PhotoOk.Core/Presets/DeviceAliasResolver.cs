using PhotoOk.Core.Metadata;

namespace PhotoOk.Core.Presets;

/// <summary>사진이 어느 기기로 찍혔는지 별칭 규칙으로 알아낸다.</summary>
public sealed class DeviceAliasResolver(IReadOnlyList<DeviceAlias> aliases)
{
    /// <summary>
    /// 모델·시리얼이 모두 맞는 별칭, 모델만 지정된 별칭, 파일 이름 접두어 별칭 순으로 찾는다.
    /// 맞는 별칭이 없으면 모델 이름(공백 제거), 그것도 없으면 null.
    /// </summary>
    public string? Resolve(PhotoInfo file)
    {
        var model = Normalize(file.CameraModel);
        var serial = Normalize(file.SerialNumber);

        if (model is not null && serial is not null)
        {
            foreach (var alias in aliases)
            {
                if (Normalize(alias.Model) is { } aliasModel && Normalize(alias.SerialNumber) is { } aliasSerial
                    && Same(aliasModel, model) && Same(aliasSerial, serial))
                {
                    return alias.Alias;
                }
            }
        }

        if (model is not null)
        {
            foreach (var alias in aliases)
            {
                if (Normalize(alias.Model) is { } aliasModel && Normalize(alias.SerialNumber) is null
                    && Same(aliasModel, model))
                {
                    return alias.Alias;
                }
            }
        }

        var fileName = Path.GetFileName(file.Path);
        foreach (var alias in aliases)
        {
            if (Normalize(alias.FileNamePrefix) is { } prefix
                && fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return alias.Alias;
            }
        }

        return model;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
