using System.Globalization;
using System.Security.Cryptography;

namespace PhotoOk.Core.Execution;

public static class JournalPaths
{
    /// <summary><c>&lt;appDataRoot&gt;\journal\&lt;runId&gt;.jsonl</c></summary>
    public static string InAppData(string appDataRoot, string runId) =>
        Path.Combine(appDataRoot, "journal", runId + ".jsonl");

    /// <summary><c>&lt;archiveRoot&gt;\.photook\journal\&lt;runId&gt;.jsonl</c></summary>
    public static string InArchive(string archiveRoot, string runId) =>
        Path.Combine(archiveRoot, ".photook", "journal", runId + ".jsonl");

    /// <summary>실행 ID: 현지 시각 <c>yyyyMMdd-HHmmss</c> + 4자리 무작위 16진수.</summary>
    public static string NewRunId(DateTime utcNow)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        var suffix = RandomNumberGenerator.GetInt32(0x10000).ToString("x4", CultureInfo.InvariantCulture);
        return local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + suffix;
    }
}
