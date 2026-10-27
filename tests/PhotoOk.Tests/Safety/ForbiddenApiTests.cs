using System.IO;

namespace PhotoOk.Tests.Safety;

/// <summary>
/// 원본을 지우거나 덮어쓰는 API가 허용된 파일 밖에서 쓰이지 않는지 검사한다.
/// 제품 규칙: 영구 삭제 없음, 덮어쓰기 없음.
/// </summary>
public sealed class ForbiddenApiTests
{
    private const string PhysicalFileSystem = "IO/PhysicalFileSystem.cs";
    private const string SafeDelete = "Execution/SafeDelete.cs";
    private const string OutsideAllowlist = "Execution/Executor.cs";

    // 규칙마다 최소 한 개의 양성 샘플. 샘플은 허용 파일 밖에 있으므로 해당 규칙으로 잡혀야 한다.
    // 뒤쪽 행은 리뷰에서 찾은 빈틈(괄호 없는 참조, 인스턴스 Delete, 위치 인수 true 등)이다.
    private static readonly (string Sample, string Pattern)[] PositiveSamples =
    [
        ("File.Delete(p);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("File.Create(p);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("File.OpenWrite(p);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("File.CreateText(p);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("paths.ForEach(File.Delete);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("Action<string> d = File.Delete;", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("File.Copy(a, b);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("File.Replace(a, b, null);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        ("File.WriteAllText(p, s);", @"\bFile\.WriteAll\w*"),
        ("File.Move(a, b);", @"\bFile\.Move\b"),
        ("File.Move(a, b, true);", @"\bFile\.Move\s*\([^;]*,\s*true\s*\)"),
        ("Write(a, overwrite: true);", @"overwrite\s*:\s*true"),
        ("Scan(p, recursive: true);", @"recursive\s*:\s*true"),
        ("info.Delete();", @"\.Delete\s*\(\s*\)"),
        ("info.Delete(true);", @"\.Delete\b"),
        ("Action a = fi.Delete;", @"\.Delete\b"),
        ("Directory.Delete(p, true);", @"\bDirectory\.Delete\s*\([^;]*,\s*true\s*\)"),
        ("Action<string> d = Directory.Delete;", @"\bDirectory\.Delete\b"),
        ("info.CopyTo(x, true);", @"\.(CopyTo|MoveTo)\s*\("),
        ("info.MoveTo(x);", @"\.(CopyTo|MoveTo)\s*\("),
        ("DeleteFile(p);", "DeleteFile"),
        ("DeleteDirectory(p);", "DeleteDirectory"),
        ("RecycleOption.SendToRecycleBin", "RecycleOption"),
        ("new FileStream(p, FileMode.Create);", @"FileMode\.(Create\b|Truncate|OpenOrCreate)"),
        ("new FileStream(p, FileMode.Truncate);", @"FileMode\.(Create\b|Truncate|OpenOrCreate)"),
        ("fs.RemoveEmptyDirectory(p);", @"\.RemoveEmptyDirectory\s*\("),
    ];

    // 허용 파일 안에서는 통과해야 하는 호출.
    private static readonly (string File, string Sample)[] AllowedSamples =
    [
        (PhysicalFileSystem, "Directory.Delete(path, recursive: false);"),
        (PhysicalFileSystem, "File.Move(source, destination, overwrite: false);"),
        (PhysicalFileSystem, "File.Move(source, destination);"),
        (PhysicalFileSystem, "new FileStream(path, FileMode.CreateNew, FileAccess.Write);"),
        (SafeDelete, "fs.RemoveEmptyDirectory(p);"),
    ];

    // 허용 파일이 있어도 허용되지 않는 규칙은 그 파일 안에서도 잡혀야 한다.
    private static readonly (string File, string Sample, string Pattern)[] AllowedFileStillFlagged =
    [
        (PhysicalFileSystem, "File.Move(source, destination, true);", @"\bFile\.Move\s*\([^;]*,\s*true\s*\)"),
        (PhysicalFileSystem, "Directory.Delete(path, recursive: true);", @"recursive\s*:\s*true"),
        (PhysicalFileSystem, "File.Delete(path);", @"\bFile\.(Delete|Copy|Replace|Create|OpenWrite|CreateText)\b"),
        (PhysicalFileSystem, "Directory.Delete(p, true);", @"\bDirectory\.Delete\s*\([^;]*,\s*true\s*\)"),
    ];

    public static IEnumerable<object[]> PositiveRows() =>
        PositiveSamples.Select(row => new object[] { row.Sample, row.Pattern });

    public static IEnumerable<object[]> AllowedRows() =>
        AllowedSamples.Select(row => new object[] { row.File, row.Sample });

    public static IEnumerable<object[]> AllowedFileStillFlaggedRows() =>
        AllowedFileStillFlagged.Select(row => new object[] { row.File, row.Sample, row.Pattern });

    [Theory]
    [MemberData(nameof(PositiveRows))]
    public void Each_Rule_Flags_Its_Positive_Sample(string sample, string pattern)
    {
        var found = ForbiddenApiScanner.Find(OutsideAllowlist, sample);

        Assert.Contains($"{OutsideAllowlist}: {pattern}", found);
    }

    [Fact]
    public void Every_Rule_Has_A_Positive_Sample()
    {
        var rulePatterns = ForbiddenApiScanner.Patterns.Order(StringComparer.Ordinal).ToList();
        var sampledPatterns = PositiveSamples.Select(row => row.Pattern).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();

        Assert.Equal(rulePatterns, sampledPatterns);
    }

    [Theory]
    [MemberData(nameof(AllowedRows))]
    public void Allowed_Calls_Pass_In_Their_Allowlisted_File(string file, string sample)
    {
        var found = ForbiddenApiScanner.Find(file, sample);

        Assert.Empty(found);
    }

    [Theory]
    [MemberData(nameof(AllowedFileStillFlaggedRows))]
    public void Unallowed_Rules_Still_Flag_Inside_Allowlisted_File(string file, string sample, string pattern)
    {
        var found = ForbiddenApiScanner.Find(file, sample);

        Assert.Contains($"{file}: {pattern}", found);
    }

    [Fact]
    public void Scanner_Flags_FileDelete()
    {
        // File.Delete(p)는 File 규칙과 인스턴스 .Delete( 규칙에 둘 다 걸린다. 여기서는 File 규칙이 잡혔는지만 본다.
        var found = ForbiddenApiScanner.Find(OutsideAllowlist, "File.Delete(p);");

        Assert.Contains($"{OutsideAllowlist}: {PositiveSamples[0].Pattern}", found);
    }

    [Fact]
    public void Scanner_Ignores_Deleted_Property()
    {
        // `\.Delete\b`는 .Deleted 같은 식별자에 걸리지 않아야 한다.
        Assert.Empty(ForbiddenApiScanner.Find(OutsideAllowlist, "var gone = entry.Deleted;"));
    }

    [Fact]
    public void Scanner_Allows_DirectoryDelete_InPhysicalFileSystem()
    {
        var found = ForbiddenApiScanner.Find(PhysicalFileSystem, "Directory.Delete(path, recursive: false);");

        Assert.Empty(found);
    }

    [Fact]
    public void Scanner_Flags_RemoveEmptyDirectory_OutsideSafeDelete()
    {
        var found = ForbiddenApiScanner.Find(OutsideAllowlist, "fs.RemoveEmptyDirectory(p);");

        Assert.Single(found);
    }

    [Fact]
    public void Scanner_Allows_RemoveEmptyDirectory_InSafeDelete()
    {
        var found = ForbiddenApiScanner.Find(SafeDelete, "fs.RemoveEmptyDirectory(p);");

        Assert.Empty(found);
    }

    [Fact]
    public void Scanner_Flags_FileModeCreate_ButNot_CreateNew()
    {
        Assert.Single(ForbiddenApiScanner.Find("IO/Writer.cs", "new FileStream(p, FileMode.Create)"));
        Assert.Empty(ForbiddenApiScanner.Find("IO/Writer.cs", "new FileStream(p, FileMode.CreateNew)"));
    }

    [Fact]
    public void SourceTree_HasNoForbiddenApis()
    {
        var sourceFiles = SourceFiles();

        // 빈 목록이면 검사가 아무것도 하지 않은 것이므로 실패로 본다.
        Assert.NotEmpty(sourceFiles);

        var violations = new List<string>();
        foreach (var (fullPath, relativePath) in sourceFiles)
        {
            violations.AddRange(ForbiddenApiScanner.Find(relativePath, File.ReadAllText(fullPath)));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ForbiddenApiScan_CoversWpfProject()
    {
        var relativePaths = SourceFiles().Select(file => file.RelativePath.Replace('\\', '/')).ToList();

        // 화면 프로젝트(App·ViewModel·서비스)도 같은 규칙으로 검사한다.
        Assert.Contains("PhotoOk/App.xaml.cs", relativePaths);
        Assert.Contains("PhotoOk/ViewModels/ShellViewModel.cs", relativePaths);
        Assert.Contains("IO/PhysicalFileSystem.cs", relativePaths);
    }

    /// <summary>
    /// 검사할 src/ 아래 모든 .cs 파일(bin·obj 제외). Core 파일은 <c>src/PhotoOk.Core/</c> 기준,
    /// 나머지(화면 프로젝트 등)는 <c>src/</c> 기준의 상대 경로를 붙인다(허용 목록이 Core 기준 경로이므로).
    /// </summary>
    private static List<(string FullPath, string RelativePath)> SourceFiles()
    {
        var srcRoot = FindSourceRoot();
        var coreRoot = Path.Combine(srcRoot, "PhotoOk.Core") + Path.DirectorySeparatorChar;
        return Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsInBuildFolder(srcRoot, file))
            .Select(file => (file, file.StartsWith(coreRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(coreRoot, file)
                : Path.GetRelativePath(srcRoot, file)))
            .ToList();
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PhotoOk.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("PhotoOk.sln이 있는 폴더를 찾을 수 없습니다.");
        }

        return Path.Combine(directory.FullName, "src");
    }

    private static bool IsInBuildFolder(string srcRoot, string file)
    {
        var segments = Path.GetRelativePath(srcRoot, file).Split(Path.DirectorySeparatorChar);
        return segments.Any(segment => segment is "bin" or "obj");
    }
}
