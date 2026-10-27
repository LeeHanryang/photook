using PhotoOk.Core.Execution;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Execution;

public class PreflightTests
{
    private const string Archive = @"D:\Archive";
    private const long Gib = 1024L * 1024 * 1024;

    private readonly FakeFileSystem _fs = new();

    private static PlannedOperation Copy(string id, string source, string destination, long length = 10) =>
        new(id, OperationKind.Copy, source, destination, length, Array.Empty<ReviewReason>());

    private static PlannedOperation Move(string id, string source, string destination) =>
        new(id, OperationKind.Move, source, destination, 10, Array.Empty<ReviewReason>());

    private static PlannedOperation Mkdir(string id, string destination) =>
        new(id, OperationKind.CreateDirectory, null, destination, 0, Array.Empty<ReviewReason>());

    private static Plan PlanOf(params PlannedOperation[] ops) =>
        new(Archive, ops, Array.Empty<PlannedSkip>(), Array.Empty<string>());

    [Fact]
    public void Preflight_NoIssues_ForHealthyPlan()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"E:\DCIM\a.jpg", new byte[] { 1 });

        var issues = Preflight.Check(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg")), _fs);

        Assert.Empty(issues);
    }

    [Fact]
    public void Preflight_Errors_WhenArchiveMissing_AndSkipsSpaceCheck()
    {
        _fs.AddFile(@"E:\DCIM\a.jpg", new byte[] { 1 });
        _fs.SetFreeSpace(@"D:\", 0);

        var issues = Preflight.Check(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg")), _fs);

        var issue = Assert.Single(issues);
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("아카이브 폴더가 없습니다", issue.Message);
    }

    [Fact]
    public void Preflight_Errors_WhenNotEnoughSpace()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"E:\DCIM\a.jpg", new byte[] { 1 });
        _fs.SetFreeSpace(@"D:\", Gib + Gib / 2); // 1.5 GB 여유

        var issues = Preflight.Check(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", Gib)), _fs);

        var issue = Assert.Single(issues);
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("대상 드라이브 공간이 부족합니다 (필요 2.0, 여유 1.5)", issue.Message);
    }

    [Fact]
    public void Preflight_Ok_WhenSpaceExactlyEnough()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"E:\DCIM\a.jpg", new byte[] { 1 });
        _fs.SetFreeSpace(@"D:\", 2 * Gib);

        var issues = Preflight.Check(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", Gib)), _fs);

        Assert.Empty(issues);
    }

    [Fact]
    public void Preflight_SpaceCheck_CountsOnlyCopyOperations()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"D:\Inbox\a.jpg", new byte[] { 1 });
        _fs.SetFreeSpace(@"D:\", Gib + 5);

        var issues = Preflight.Check(PlanOf(Move("op1", @"D:\Inbox\a.jpg", @"D:\Archive\2026\a.jpg")), _fs);

        Assert.Empty(issues);
    }

    [Fact]
    public void Preflight_Errors_WhenSourceMissing()
    {
        _fs.AddDirectory(Archive);

        var issues = Preflight.Check(PlanOf(
            Copy("op1", @"E:\DCIM\gone.jpg", @"D:\Archive\2026\gone.jpg"),
            Move("op2", @"E:\DCIM\gone2.jpg", @"D:\Archive\2026\gone2.jpg")), _fs);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i =>
        {
            Assert.Equal(Severity.Error, i.Severity);
            Assert.Equal("원본 파일을 찾을 수 없습니다", i.Message);
        });
        Assert.Equal(@"E:\DCIM\gone.jpg", issues[0].Path);
        Assert.Equal(@"E:\DCIM\gone2.jpg", issues[1].Path);
    }

    [Fact]
    public void Preflight_Warns_OnLongPath()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"E:\DCIM\a.jpg", new byte[] { 1 });
        var longDestination = @"D:\Archive\" + new string('가', 250) + @"\a.jpg";

        var issues = Preflight.Check(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", longDestination)), _fs);

        var issue = Assert.Single(issues);
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Equal("경로가 길어 일부 프로그램에서 열리지 않을 수 있습니다", issue.Message);
        Assert.Equal(longDestination, issue.Path);
    }

    [Fact]
    public void Preflight_NoWarning_ForPathOfExactly259Chars()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"E:\DCIM\a.jpg", new byte[] { 1 });
        var prefix = @"D:\Archive\";
        var destination = prefix + new string('a', 259 - prefix.Length);
        Assert.Equal(259, destination.Length);

        var issues = Preflight.Check(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", destination)), _fs);

        Assert.Empty(issues);
    }

    [Fact]
    public void Preflight_Warns_OnLongCreateDirectoryDestination()
    {
        _fs.AddDirectory(Archive);
        var longDirectory = @"D:\Archive\" + new string('b', 260);

        var issues = Preflight.Check(PlanOf(Mkdir("op1", longDirectory)), _fs);

        var issue = Assert.Single(issues);
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Equal(longDirectory, issue.Path);
    }

    [Fact]
    public void Preflight_AcceptsSourceProducedByEarlierOperation()
    {
        // 자리 바꾸기: 두 번째 다리의 원본(임시 이름)은 앞 작업이 만들어 낸다.
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"D:\Archive\15\P.JPG", new byte[] { 1 });
        _fs.AddFile(@"D:\Archive\14\P.JPG", new byte[] { 2 });
        const string TempA = @"D:\Archive\15\P.JPG.photook-tmp-op1";
        const string TempB = @"D:\Archive\14\P.JPG.photook-tmp-op2";

        var issues = Preflight.Check(PlanOf(
            Move("op1", @"D:\Archive\15\P.JPG", TempA),
            Move("op2", @"D:\Archive\14\P.JPG", TempB),
            Move("op3", TempA, @"D:\Archive\14\P.JPG"),
            Move("op4", TempB, @"D:\Archive\15\P.JPG")), _fs);

        Assert.Empty(issues);
    }

    [Fact]
    public void Preflight_StillErrors_WhenSourceIsProducedOnlyByLaterOperation()
    {
        _fs.AddDirectory(Archive);
        _fs.AddFile(@"D:\Inbox\a.jpg", new byte[] { 1 });

        var issues = Preflight.Check(PlanOf(
            Move("op1", @"D:\Archive\x.jpg", @"D:\Archive\y.jpg"),
            Move("op2", @"D:\Inbox\a.jpg", @"D:\Archive\x.jpg")), _fs);

        var issue = Assert.Single(issues);
        Assert.Equal(@"D:\Archive\x.jpg", issue.Path);
    }
}
