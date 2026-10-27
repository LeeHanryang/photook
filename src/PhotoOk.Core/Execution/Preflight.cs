using System.Globalization;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

public enum Severity { Warning, Error }

public sealed record PreflightIssue(Severity Severity, string Message, string? Path = null);

/// <summary>실행 전에 계획이 안전하게 끝날 수 있는지 점검한다. Error가 하나라도 있으면 시작하지 않는다.</summary>
public static class Preflight
{
    private const long ReserveBytes = 1024L * 1024 * 1024;
    private const double BytesPerGb = 1024.0 * 1024 * 1024;
    private const int MaxPathLength = 259;

    public static IReadOnlyList<PreflightIssue> Check(Plan plan, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var issues = new List<PreflightIssue>();

        if (!fileSystem.DirectoryExists(plan.ArchiveRoot))
        {
            issues.Add(new PreflightIssue(Severity.Error, "아카이브 폴더가 없습니다"));
        }
        else
        {
            var needed = ReserveBytes;
            foreach (var op in plan.Operations)
            {
                if (op.Kind == OperationKind.Copy)
                {
                    needed += op.Length;
                }
            }

            var available = fileSystem.GetAvailableFreeSpace(plan.ArchiveRoot);
            if (needed > available)
            {
                issues.Add(new PreflightIssue(Severity.Error,
                    $"대상 드라이브 공간이 부족합니다 (필요 {FormatGb(needed)}, 여유 {FormatGb(available)})"));
            }
        }

        // 자리 바꾸기의 두 번째 다리처럼, 앞 작업이 만들어 내는 경로를 원본으로 쓰는 작업은 실행 전에 원본이 없는 것이 정상이다.
        var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in plan.Operations)
        {
            if (op.Kind != OperationKind.CreateDirectory && op.Source is not null
                && !produced.Contains(Path.GetFullPath(op.Source)) && !fileSystem.FileExists(op.Source))
            {
                issues.Add(new PreflightIssue(Severity.Error, "원본 파일을 찾을 수 없습니다", op.Source));
            }

            if (op.Kind != OperationKind.CreateDirectory)
            {
                produced.Add(Path.GetFullPath(op.Destination));
            }

            if (op.Destination.Length > MaxPathLength)
            {
                issues.Add(new PreflightIssue(Severity.Warning, "경로가 길어 일부 프로그램에서 열리지 않을 수 있습니다", op.Destination));
            }
        }

        return issues;
    }

    private static string FormatGb(long bytes) =>
        (bytes / BytesPerGb).ToString("0.0", CultureInfo.InvariantCulture);
}
