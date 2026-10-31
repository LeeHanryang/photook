using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Workflow;

namespace PhotoOk.Core.Settings;

/// <summary>
/// 마지막 작업의 선택. 다음에 앱을 열 때 그대로 보여 준다. 뒤의 네 값(흡수·재정리의 이름 옵션)은 옛 설정 줄에는 없으므로
/// 읽을 때 기본값이 된다.
/// </summary>
public sealed record AppSettings(IReadOnlyList<string> Sources, bool IncludeSubfolders, string? ArchiveRoot,
    string? PresetName, string? PresetFile, TransferMode Mode, bool UseFolderNames = true, bool SplitUnderscores = false,
    SubfolderPolicy Subfolders = SubfolderPolicy.Keep, bool KeepReviewInPlace = true)
{
    public static AppSettings Default { get; } = new([], true, null, "날짜 + 이벤트", null, TransferMode.Copy);
}

/// <summary>
/// 설정 파일은 덮어쓰지 않는다. 저장할 때마다 JSON 한 줄을 덧붙이고, 읽을 때는 마지막 줄부터 거꾸로 처음 해석되는 줄을 쓴다.
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>파일이 없거나, 읽을 권한이 없거나, 읽을 수 있는 줄이 없으면 <see cref="AppSettings.Default"/>.</summary>
    public static AppSettings Load(IFileSystem fileSystem, string path)
    {
        try
        {
            return ReadLast(fileSystem, path).Settings ?? AppSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 설정을 못 읽어도 앱은 기본값으로 시작한다.
            return AppSettings.Default;
        }
    }

    /// <summary>
    /// 마지막으로 해석되는 줄. 파일이 없거나 해석되는 줄이 없으면 <c>Settings</c>는 null이고, <c>HasLines</c>는 빈 줄이 아닌 줄이 있었는지를 알려 준다.
    /// 파일을 읽지 못하면 예외가 그대로 나간다.
    /// </summary>
    private static (AppSettings? Settings, bool HasLines) ReadLast(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.FileExists(path))
        {
            return (null, false);
        }

        string text;
        using (var stream = fileSystem.OpenRead(path))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        var hasLines = false;
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            hasLines = true;
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(line, JsonOptions);
                if (settings?.Sources is not null)
                {
                    return (settings, true);
                }
            }
            catch (JsonException)
            {
                // 쓰다 만 줄이거나 읽을 수 없는 줄이면 바로 앞 줄을 쓴다.
            }
        }

        return (null, hasLines);
    }

    /// <summary>부모 폴더를 만들고 JSON 한 줄을 덧붙인다. 앞의 줄은 그대로 남는다.</summary>
    public static void Save(IFileSystem fileSystem, string path, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            fileSystem.CreateDirectory(parent);
        }

        fileSystem.AppendLine(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// 흡수·재정리는 가져오기의 선택을 저장된 값에서 가져와 다시 쓴다. 그 값을 읽을 수 없으면 기본값으로 가리지 않도록 저장하기 전에 부른다:
    /// 파일을 읽지 못하면 그 예외가, 줄이 있는데 하나도 해석되지 않으면 <see cref="IOException"/>이 나간다. 파일이 없거나 비어 있으면 가릴 것이 없어 그냥 돌아온다.
    /// </summary>
    public static void EnsureSavedChoicesReadable(IFileSystem fileSystem, string path)
    {
        if (ReadLast(fileSystem, path) is { Settings: null, HasLines: true })
        {
            throw new IOException("설정 파일에 해석되는 줄이 없어 선택을 저장하지 않았습니다: " + path);
        }
    }

    /// <summary>
    /// 실행할 때 미리보기의 최종 선택을 한 줄 덧붙인다. 흡수·재정리는 가져오기의 폴더·하위 폴더 포함·복사/이동을 저장된 값 그대로 둔다
    /// (다음 가져오기에 영향을 주지 않게). 파일이 있는데 읽지 못하면 아무것도 쓰지 않고 예외(<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>)를 그대로 던진다: 기본값과 합쳐 쓰면 가져오기의 선택을 덮어 버린다.
    /// 파일에 줄이 있는데 하나도 해석되지 않을 때도 같은 이유로 쓰지 않고 <see cref="IOException"/>을 던진다. 가져오기는 모든 값을 자기 값으로 쓰므로 합칠 것이 없어 그대로 쓴다.
    /// </summary>
    public static void SaveChoices(IFileSystem fileSystem, string path, ImportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var isImport = settings.Kind == JobKind.Import;
        if (!isImport)
        {
            EnsureSavedChoicesReadable(fileSystem, path);
        }

        var last = ReadLast(fileSystem, path).Settings ?? AppSettings.Default;
        Save(fileSystem, path, last with
        {
            Sources = isImport ? settings.Sources : last.Sources,
            IncludeSubfolders = isImport ? settings.IncludeSubfolders : last.IncludeSubfolders,
            Mode = isImport ? settings.Mode : last.Mode,
            ArchiveRoot = settings.ArchiveRoot,
            PresetName = settings.Preset.Name,
            PresetFile = settings.PresetFile,
            UseFolderNames = settings.UseFolderNames,
            SplitUnderscores = settings.SplitUnderscores,
            Subfolders = settings.Subfolders,
            KeepReviewInPlace = settings.KeepReviewInPlace,
        });
    }
}
