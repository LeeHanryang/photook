using System.Text.RegularExpressions;

namespace PhotoOk.Tests.Safety;

/// <summary>
/// 삭제·덮어쓰기에 쓰는 API 패턴이 허용된 파일 밖에서 나오면 위반으로 돌려준다.
/// 경로는 <c>src/PhotoOk.Core/</c> 기준의 상대 경로이며 <c>/</c>로 구분한다.
/// 주석 안의 문자열도 그대로 검사한다.
/// </summary>
internal static class ForbiddenApiScanner
{
    private sealed record Rule(string Pattern, string[] AllowedFiles);

    private static readonly Rule[] Rules =
    [
        new(@"\bFile\.Delete\s*\(", []),
        new(@"\.Delete\s*\(\s*\)", []),
        new("DeleteFile", []),
        new("DeleteDirectory", []),
        new("RecycleOption", []),
        new(@"\bDirectory\.Delete\s*\(", ["IO/PhysicalFileSystem.cs"]),
        new(@"\bFile\.Move\s*\(", ["IO/PhysicalFileSystem.cs"]),
        new(@"\bFile\.(Copy|Replace|WriteAll\w*)\s*\(", []),
        new(@"FileMode\.(Create\b|Truncate|OpenOrCreate)", []),
        new(@"overwrite\s*:\s*true", []),
        new(@"\.RemoveEmptyDirectory\s*\(", ["Execution/SafeDelete.cs"]),
    ];

    /// <summary>
    /// <paramref name="sourceText"/>에서 금지 패턴을 찾는다.
    /// 같은 파일에서 같은 패턴이 여러 번 나와도 위반은 패턴당 한 번만 기록한다.
    /// </summary>
    /// <returns>위반마다 <c>"&lt;경로&gt;: &lt;패턴&gt;"</c> 형식의 문자열. 위반이 없으면 빈 목록.</returns>
    public static IReadOnlyList<string> Find(string relativePath, string sourceText)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var violations = new List<string>();

        foreach (var rule in Rules)
        {
            if (rule.AllowedFiles.Contains(normalizedPath, StringComparer.Ordinal))
            {
                continue;
            }

            if (Regex.IsMatch(sourceText, rule.Pattern))
            {
                violations.Add($"{normalizedPath}: {rule.Pattern}");
            }
        }

        return violations;
    }
}
