using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Settings;

/// <summary>마지막 작업의 선택. 다음에 앱을 열 때 그대로 보여 준다.</summary>
public sealed record AppSettings(IReadOnlyList<string> Sources, bool IncludeSubfolders, string? ArchiveRoot,
    string? PresetName, string? PresetFile, TransferMode Mode)
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
        string text;
        try
        {
            if (!fileSystem.FileExists(path))
            {
                return AppSettings.Default;
            }

            using var stream = fileSystem.OpenRead(path);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 설정을 못 읽어도 앱은 기본값으로 시작한다.
            return AppSettings.Default;
        }

        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(line, JsonOptions);
                if (settings?.Sources is not null)
                {
                    return settings;
                }
            }
            catch (JsonException)
            {
                // 쓰다 만 줄이거나 읽을 수 없는 줄이면 바로 앞 줄을 쓴다.
            }
        }

        return AppSettings.Default;
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
}
