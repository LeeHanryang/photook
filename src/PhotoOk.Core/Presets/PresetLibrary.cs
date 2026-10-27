using System.IO;
using System.Text;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Presets;

/// <summary>목록에 보여 줄 프리셋 한 개. 내장 프리셋은 <paramref name="FilePath"/>가 null이다.</summary>
public sealed record PresetEntry(string Name, Preset Preset, string Description, string? FilePath);

/// <summary>읽지 못한 프리셋 파일(또는 폴더)과 그 이유.</summary>
public sealed record PresetLoadError(string FilePath, string Message);

/// <summary>내장 프리셋과 사용자 프리셋 폴더의 <c>*.json</c> 파일을 모아 보여 준다. 파일은 읽기만 한다.</summary>
public sealed class PresetLibrary(IFileSystem fileSystem, string userPresetFolder)
{
    public static IReadOnlyList<PresetEntry> BuiltIn { get; } =
    [
        new("날짜 + 이벤트", BuiltInPresets.DateEvent, "날짜순으로 정리하고, 이름을 적은 날만 폴더 이름에 붙습니다.", null),
        new("날짜만", BuiltInPresets.DateOnly, "완전 자동입니다. 폴더 이름만으로는 내용을 알 수 없습니다.", null),
        new("날짜·기기·포맷", BuiltInPresets.DateDeviceFormat, "카메라와 RAW/JPG별로 나뉩니다. 폴더가 깊어집니다.", null),
        new("월 단위", BuiltInPresets.Monthly, "폴더 수가 적습니다. 한 폴더에 사진이 많아집니다.", null),
    ];

    /// <summary>내장 4개, 이어서 사용자 폴더의 <c>*.json</c>(하위 폴더 제외, 파일 이름순). 읽지 못한 파일은 건너뛰고 오류로 모은다.</summary>
    public (IReadOnlyList<PresetEntry> Presets, IReadOnlyList<PresetLoadError> Errors) LoadAll()
    {
        var presets = new List<PresetEntry>(BuiltIn);
        var errors = new List<PresetLoadError>();

        if (!fileSystem.DirectoryExists(userPresetFolder))
        {
            return (presets, errors);
        }

        List<string> files;
        try
        {
            files = fileSystem
                .EnumerateFiles(userPresetFolder, recursive: false,
                    onInaccessible: (folder, _) => errors.Add(new PresetLoadError(folder, "프리셋 폴더를 열 수 없습니다")))
                .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new PresetLoadError(userPresetFolder, "프리셋 폴더를 열 수 없습니다"));
            return (presets, errors);
        }

        foreach (var path in files)
        {
            try
            {
                presets.Add(LoadFile(path));
            }
            catch (PresetException ex)
            {
                errors.Add(new PresetLoadError(path, "프리셋을 읽을 수 없습니다: " + ex.Message));
            }
        }

        return (presets, errors);
    }

    /// <summary>프리셋 파일 하나를 읽는다. 읽기나 해석에 실패하면 한국어 메시지의 <see cref="PresetException"/>.</summary>
    public PresetEntry LoadFile(string path)
    {
        string json;
        try
        {
            using var stream = fileSystem.OpenRead(path);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PresetException($"프리셋 파일을 읽을 수 없습니다: {path}");
        }

        var preset = PresetSerializer.Deserialize(json);
        return new PresetEntry(preset.Name, preset, "사용자 프리셋: " + Path.GetFileName(path), path);
    }
}
