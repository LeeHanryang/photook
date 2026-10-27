using System.IO;

namespace PhotoOk.Tests.Safety;

/// <summary>
/// 원본을 지우거나 덮어쓰는 API가 허용된 파일 밖에서 쓰이지 않는지 검사한다.
/// 제품 규칙: 영구 삭제 없음, 덮어쓰기 없음.
/// </summary>
public sealed class ForbiddenApiTests
{
    [Fact]
    public void Scanner_Flags_FileDelete()
    {
        var found = ForbiddenApiScanner.Find("Execution/Executor.cs", "File.Delete(p);");

        Assert.Single(found);
    }

    [Fact]
    public void Scanner_Allows_DirectoryDelete_InPhysicalFileSystem()
    {
        var found = ForbiddenApiScanner.Find("IO/PhysicalFileSystem.cs", "Directory.Delete(path, recursive: false);");

        Assert.Empty(found);
    }

    [Fact]
    public void Scanner_Flags_RemoveEmptyDirectory_OutsideSafeDelete()
    {
        var found = ForbiddenApiScanner.Find("Execution/Executor.cs", "fs.RemoveEmptyDirectory(p);");

        Assert.Single(found);
    }

    [Fact]
    public void Scanner_Allows_RemoveEmptyDirectory_InSafeDelete()
    {
        var found = ForbiddenApiScanner.Find("Execution/SafeDelete.cs", "fs.RemoveEmptyDirectory(p);");

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
        var srcRoot = FindSourceRoot();
        var sourceFiles = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsInBuildFolder(srcRoot, file))
            .ToList();

        // 빈 목록이면 검사가 아무것도 하지 않은 것이므로 실패로 본다.
        Assert.NotEmpty(sourceFiles);

        var coreRoot = Path.Combine(srcRoot, "PhotoOk.Core") + Path.DirectorySeparatorChar;
        var violations = new List<string>();
        foreach (var file in sourceFiles)
        {
            var relativePath = file.StartsWith(coreRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(coreRoot, file)
                : Path.GetRelativePath(srcRoot, file);

            violations.AddRange(ForbiddenApiScanner.Find(relativePath, File.ReadAllText(file)));
        }

        Assert.Empty(violations);
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
