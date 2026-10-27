using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoOk.Core.Layout;

namespace PhotoOk.Core.Presets;

public static class PresetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(Preset preset) => JsonSerializer.Serialize(preset, Options);

    public static Preset Deserialize(string json)
    {
        Preset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<Preset>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new PresetException($"프리셋 파일을 읽을 수 없습니다: {ex.Message}");
        }

        if (preset is null)
        {
            throw new PresetException("프리셋 파일이 비어 있습니다");
        }

        Validate(preset);
        return preset;
    }

    private static void Validate(Preset preset)
    {
        if (preset.Branches is null || preset.Branches.Count == 0
            || preset.Branches[^1] is not { When: BranchCondition.Always })
        {
            throw new PresetException("마지막 분기는 조건 없는 기본 분기여야 합니다");
        }

        if (preset.Branches.Any(branch => branch is null || branch.Levels is null))
        {
            throw new PresetException("분기에 폴더 단계가 지정되어 있지 않습니다");
        }

        if (preset.DeviceAliases is null || preset.FormatGroups is null)
        {
            throw new PresetException("기기 별칭 또는 포맷 묶음이 지정되어 있지 않습니다");
        }

        foreach (var level in preset.Branches.SelectMany(branch => branch.Levels))
        {
            if (level?.Template is null)
            {
                throw new PresetException("폴더 단계의 템플릿이 지정되어 있지 않습니다");
            }

            try
            {
                PathTemplate.Parse(level.Template);
            }
            catch (TemplateException ex)
            {
                throw new PresetException(ex.Message);
            }
        }
    }
}
