using System.IO;
using System.Text;
using PhotoOk.Core.IO;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.IO;

public sealed class FakeFileSystemTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static void Write(Stream stream, params string[] chunks)
    {
        using (stream)
        {
            foreach (var c in chunks)
            {
                stream.Write(B(c));
            }
        }
    }

    // ---- 계약에서 요구한 핵심 동작 ----

    [Fact]
    public void CrashAfter_ThrowsOnNextMutatingOp()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a\x.jpg", B("x"));
        fs.CrashAfter(2);

        fs.CreateDirectory(@"D:\out\1");
        fs.CreateDirectory(@"D:\out\2");

        Assert.Throws<SimulatedCrashException>(() => fs.Move(@"D:\a\x.jpg", @"D:\out\1\x.jpg"));
        Assert.True(fs.FileExists(@"D:\a\x.jpg"));
        Assert.False(fs.FileExists(@"D:\out\1\x.jpg"));
    }

    [Fact]
    public void Paths_AreCaseInsensitive()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\Photos\IMG.JPG", B("x"));

        Assert.True(fs.FileExists(@"d:\photos\img.jpg"));
        Assert.True(fs.DirectoryExists(@"D:\PHOTOS"));
        Assert.Equal(B("x"), fs.ReadAllBytes(@"d:\PHOTOS\img.jpg"));
        Assert.Throws<IOException>(() => fs.CreateNew(@"d:\photos\img.jpg"));
    }

    [Fact]
    public void RemoveVolume_MakesPathsUnavailable()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"E:\a.jpg", B("x"));

        fs.RemoveVolume(@"E:\");

        Assert.False(fs.FileExists(@"E:\a.jpg"));
        Assert.False(fs.DirectoryExists(@"E:\"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.OpenRead(@"E:\a.jpg"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.CreateNew(@"E:\b.jpg"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.Move(@"E:\a.jpg", @"E:\c.jpg"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.GetFile(@"E:\a.jpg"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.AppendLine(@"E:\log.txt", "x"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.CreateDirectory(@"E:\d"));
    }

    [Fact]
    public void RestoreVolume_ReconnectsWithContentIntact()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"E:\a.jpg", B("x"));
        fs.RemoveVolume("E:");

        fs.RestoreVolume(@"e:\");

        Assert.True(fs.FileExists(@"E:\a.jpg"));
        Assert.Equal(B("x"), fs.ReadAllBytes(@"E:\a.jpg"));
    }

    // ---- 경로 정규화와 디렉터리 ----

    [Fact]
    public void Paths_AreNormalized_TrailingSeparatorsAndDotSegments()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a\b\x.jpg", B("x"));

        Assert.True(fs.DirectoryExists(@"D:\a\b\"));
        Assert.True(fs.FileExists(@"D:\a\c\..\b\x.jpg"));
        Assert.True(fs.FileExists("D:/a/b/x.jpg"));
    }

    [Fact]
    public void RelativePaths_AreRejected()
    {
        var fs = new FakeFileSystem();

        Assert.Throws<ArgumentException>(() => fs.FileExists(@"a\b.jpg"));
    }

    [Fact]
    public void AddFile_CreatesParentDirectories_AndVolumeRoot()
    {
        var fs = new FakeFileSystem();

        fs.AddFile(@"E:\a\b\x.jpg", B("x"));

        Assert.True(fs.DirectoryExists(@"E:\a\b"));
        Assert.True(fs.DirectoryExists(@"E:\a"));
        Assert.True(fs.DirectoryExists(@"E:\"));
        Assert.False(fs.DirectoryExists(@"F:\"));
    }

    [Fact]
    public void CreateDirectory_CreatesParents_AndIsIdempotent()
    {
        var fs = new FakeFileSystem();

        fs.CreateDirectory(@"D:\a\b\c");
        fs.CreateDirectory(@"D:\a\b\c");

        Assert.True(fs.DirectoryExists(@"D:\a\b"));
        Assert.True(fs.DirectoryExists(@"D:\a\b\c"));
        Assert.False(fs.FileExists(@"D:\a\b\c"));
    }

    [Fact]
    public void CreateDirectory_Throws_WhenFileInTheWay()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a", B("x"));

        Assert.Throws<IOException>(() => fs.CreateDirectory(@"D:\a"));
        Assert.Throws<IOException>(() => fs.CreateDirectory(@"D:\a\b"));
    }

    [Fact]
    public void Directory_KeepsFirstCreatedCasing_InEnumeration()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\Photos\a.jpg", B("1"));
        fs.AddFile(@"D:\photos\b.jpg", B("2"));

        var files = fs.EnumerateFiles(@"D:\Photos", recursive: false).ToList();

        Assert.Equal(new[] { @"D:\Photos\a.jpg", @"D:\Photos\b.jpg" }, files);
    }

    // ---- 열거 ----

    [Fact]
    public void EnumerateFiles_ReturnsFullPaths_RespectingRecursive()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\in\a.jpg", B("1"));
        fs.AddFile(@"D:\in\sub\b.jpg", B("2"));
        fs.AddFile(@"D:\other\c.jpg", B("3"));

        var flat = fs.EnumerateFiles(@"D:\in", recursive: false).ToList();
        var deep = fs.EnumerateFiles(@"D:\in\", recursive: true).ToList();

        Assert.Equal(new[] { @"D:\in\a.jpg" }, flat);
        Assert.Equal(new[] { @"D:\in\a.jpg", @"D:\in\sub\b.jpg" }, deep);
    }

    [Fact]
    public void EnumerateFiles_DoesNotMatchSiblingWithSamePrefix()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\in\a.jpg", B("1"));
        fs.AddFile(@"D:\in2\b.jpg", B("2"));

        Assert.Equal(new[] { @"D:\in\a.jpg" }, fs.EnumerateFiles(@"D:\in", recursive: true).ToList());
    }

    [Fact]
    public void EnumerateFiles_DeniedDirectory_IsSkippedAndReported_OutermostOnly()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\in\a.jpg", [1]);
        fs.AddFile(@"D:\in\Locked\b.jpg", [2]);
        fs.AddFile(@"D:\in\Locked\Deeper\c.jpg", [3]);
        fs.AddDirectory(@"D:\in\EmptyLocked");
        fs.DenyDirectory(@"D:\in\Locked");
        fs.DenyDirectory(@"D:\in\Locked\Deeper");
        fs.DenyDirectory(@"D:\in\EmptyLocked");

        var skipped = new List<string>();
        var files = fs.EnumerateFiles(@"D:\in", recursive: true, (path, ex) =>
        {
            Assert.IsType<UnauthorizedAccessException>(ex);
            skipped.Add(path);
        }).ToList();

        Assert.Equal([@"D:\in\a.jpg"], files);
        Assert.Equal([@"D:\in\EmptyLocked", @"D:\in\Locked"], skipped);
        Assert.True(fs.DirectoryExists(@"D:\in\Locked"));
    }

    [Fact]
    public void EnumerateFiles_DeniedRoot_IsReported_AndWithoutCallbackThrows()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\in\a.jpg", [1]);
        fs.DenyDirectory(@"D:\in");

        var skipped = new List<string>();
        Assert.Empty(fs.EnumerateFiles(@"D:\in", recursive: false, (path, _) => skipped.Add(path)));
        Assert.Equal([@"D:\in"], skipped);
        Assert.Throws<UnauthorizedAccessException>(() => fs.EnumerateFiles(@"D:\in", recursive: true).ToList());
    }

    [Fact]
    public void EnumerateFiles_Throws_WhenDirectoryMissing()
    {
        var fs = new FakeFileSystem();

        Assert.Throws<DirectoryNotFoundException>(() => fs.EnumerateFiles(@"D:\none", recursive: true).ToList());
    }

    [Fact]
    public void EnumerateFiles_ReturnsSnapshot_SoMovingWhileEnumeratingIsSafe()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\in\a.jpg", B("1"));
        fs.AddFile(@"D:\in\b.jpg", B("2"));
        fs.CreateDirectory(@"D:\out");

        foreach (var f in fs.EnumerateFiles(@"D:\in", recursive: true))
        {
            fs.Move(f, @"D:\out\" + Path.GetFileName(f));
        }

        Assert.Equal(2, fs.AllFiles().Count);
    }

    // ---- GetFile / OpenRead ----

    [Fact]
    public void GetFile_ReturnsLengthAndTime_AndDefaultTimeIsFixed()
    {
        var fs = new FakeFileSystem();
        var stamp = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        fs.AddFile(@"D:\a.jpg", B("abc"), stamp);
        fs.AddFile(@"D:\b.jpg", B("abcd"));

        var a = fs.GetFile(@"D:\a.jpg");
        var b = fs.GetFile(@"d:\B.JPG");

        Assert.Equal(new FileEntry(@"D:\a.jpg", 3, stamp), a);
        Assert.Equal(4, b.Length);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), b.LastWriteTimeUtc);
    }

    [Fact]
    public void GetFile_Throws_WhenMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a");

        Assert.Throws<FileNotFoundException>(() => fs.GetFile(@"D:\a\none.jpg"));
    }

    [Fact]
    public void OpenRead_ReturnsReadOnlyStream_WithContent()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.jpg", B("hello"));

        using var s = fs.OpenRead(@"D:\a.jpg");
        using var reader = new StreamReader(s);

        Assert.False(s.CanWrite);
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public void OpenRead_Throws_WhenFileMissing_OrDirectoryMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a");

        Assert.Throws<FileNotFoundException>(() => fs.OpenRead(@"D:\a\none.jpg"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.OpenRead(@"D:\zzz\none.jpg"));
    }

    [Fact]
    public void FailReadsOf_MakesOpenReadThrowIOException_OnlyForThatPath()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\bad.jpg", B("x"));
        fs.AddFile(@"D:\good.jpg", B("y"));
        fs.FailReadsOf(@"d:\BAD.jpg");

        Assert.Throws<IOException>(() => fs.OpenRead(@"D:\bad.jpg"));
        using var ok = fs.OpenRead(@"D:\good.jpg");
        Assert.True(fs.FileExists(@"D:\bad.jpg"));
    }

    // ---- CreateNew ----

    [Fact]
    public void CreateNew_Throws_WhenFileExists_AndKeepsFirstContent()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"D:\");
        Write(fs.CreateNew(@"D:\a.bin"), "first");

        Assert.Throws<IOException>(() => fs.CreateNew(@"D:\a.bin"));

        Assert.Equal(B("first"), fs.ReadAllBytes(@"D:\a.bin"));
    }

    [Fact]
    public void CreateNew_Throws_WhenParentDirectoryMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a");

        Assert.Throws<DirectoryNotFoundException>(() => fs.CreateNew(@"D:\nope\a.bin"));
        Assert.False(fs.FileExists(@"D:\nope\a.bin"));
    }

    [Fact]
    public void CreateNew_ContentBecomesVisibleAsItIsWritten()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"D:\");

        using var stream = fs.CreateNew(@"D:\a.bin");
        Assert.True(fs.FileExists(@"D:\a.bin"));
        Assert.Equal(0, fs.GetFile(@"D:\a.bin").Length);

        stream.Write(B("abc"));
        Assert.Equal(B("abc"), fs.ReadAllBytes(@"D:\a.bin"));

        stream.Write(B("de"));
        Assert.Equal(B("abcde"), fs.ReadAllBytes(@"D:\a.bin"));
    }

    [Fact]
    public void CreateNew_Throws_WhenPathIsExistingDirectory()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a");

        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateNew(@"D:\a"));
    }

    [Fact]
    public void CreateNewStream_IsWriteOnly_AndRejectsWritesAfterDispose()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"D:\");
        var stream = fs.CreateNew(@"D:\a.bin");

        Assert.True(stream.CanWrite);
        Assert.False(stream.CanRead);
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Write(B("x")));
    }

    [Fact]
    public void CrashDuringWrite_LeavesPartialFile()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\out");
        var stream = fs.CreateNew(@"D:\out\a.bin"); // 변경 작업 2번째 (CreateDirectory 포함)
        fs.CrashAfter(1);

        stream.Write(B("part1"));
        Assert.Throws<SimulatedCrashException>(() => stream.Write(B("part2")));

        Assert.Equal(B("part1"), fs.ReadAllBytes(@"D:\out\a.bin"));
    }

    [Fact]
    public void CorruptNextWrite_FlipsFirstByteOfNextCreateNewStreamOnly()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"D:\");
        fs.CorruptNextWrite();

        Write(fs.CreateNew(@"D:\a.bin"), "AB", "CD");
        Write(fs.CreateNew(@"D:\b.bin"), "AB");

        var a = fs.ReadAllBytes(@"D:\a.bin");
        Assert.Equal((byte)(B("A")[0] ^ 0xFF), a[0]);
        Assert.Equal(B("BCD"), a[1..]);
        Assert.Equal(B("AB"), fs.ReadAllBytes(@"D:\b.bin"));
    }

    // ---- Move ----

    [Fact]
    public void Move_MovesFile_AndPreservesContentAndTime()
    {
        var fs = new FakeFileSystem();
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        fs.AddFile(@"D:\a\x.jpg", B("x"), stamp);
        fs.CreateDirectory(@"D:\b");

        fs.Move(@"D:\a\x.jpg", @"D:\b\y.jpg");

        Assert.False(fs.FileExists(@"D:\a\x.jpg"));
        Assert.Equal(B("x"), fs.ReadAllBytes(@"D:\b\y.jpg"));
        Assert.Equal(stamp, fs.GetFile(@"D:\b\y.jpg").LastWriteTimeUtc);
    }

    [Fact]
    public void Move_Throws_AndKeepsBoth_WhenDestinationExists()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\src.txt", B("source"));
        fs.AddFile(@"D:\dst.txt", B("destination"));

        Assert.Throws<IOException>(() => fs.Move(@"D:\src.txt", @"D:\DST.TXT"));

        Assert.Equal(B("source"), fs.ReadAllBytes(@"D:\src.txt"));
        Assert.Equal(B("destination"), fs.ReadAllBytes(@"D:\dst.txt"));
    }

    [Fact]
    public void Move_Throws_WhenSourceMissing_OrDestinationDirectoryMissing()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\src.txt", B("source"));

        Assert.Throws<FileNotFoundException>(() => fs.Move(@"D:\none.txt", @"D:\x.txt"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.Move(@"D:\src.txt", @"D:\nope\x.txt"));
        Assert.True(fs.FileExists(@"D:\src.txt"));
    }

    [Fact]
    public void Move_CaseOnlyRename_Succeeds_AndKeepsContent()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.txt", B("x"));

        fs.Move(@"D:\a.txt", @"D:\A.TXT");

        Assert.Equal(new[] { @"D:\A.TXT" }, fs.AllFiles());
        Assert.Equal(B("x"), fs.ReadAllBytes(@"D:\a.txt"));
    }

    [Fact]
    public void Move_Throws_WhileSourceIsOpenForRead_OrBeingWritten()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.txt", B("x"));

        using (fs.OpenRead(@"D:\a.txt"))
        {
            Assert.Throws<IOException>(() => fs.Move(@"D:\a.txt", @"D:\b.txt"));
        }

        fs.Move(@"D:\a.txt", @"D:\b.txt");

        using (fs.CreateNew(@"D:\c.txt"))
        {
            Assert.Throws<IOException>(() => fs.Move(@"D:\c.txt", @"D:\d.txt"));
            Assert.Throws<IOException>(() => fs.OpenRead(@"D:\c.txt"));
        }

        fs.Move(@"D:\c.txt", @"D:\d.txt");
        Assert.Equal(new[] { @"D:\b.txt", @"D:\d.txt" }, fs.AllFiles());
    }

    [Fact]
    public void Crash_ReleasesOpenHandles_LikeAProcessExit()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.txt", B("x"));
        var reader = fs.OpenRead(@"D:\a.txt");
        var writer = fs.CreateNew(@"D:\w.txt"); // 닫지 않은 채 크래시
        fs.CrashAfter(0);
        Assert.Throws<SimulatedCrashException>(() => fs.CreateDirectory(@"D:\x"));
        fs.ClearFault();

        fs.Move(@"D:\a.txt", @"D:\b.txt");
        fs.Move(@"D:\w.txt", @"D:\w2.txt");
        using (fs.OpenRead(@"D:\w2.txt")) { }
        Assert.Throws<ObjectDisposedException>(() => writer.Write(B("late")));
        GC.KeepAlive(reader);
    }

    [Fact]
    public void Move_Throws_WhenDestinationIsExistingDirectory()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.txt", B("x"));
        fs.CreateDirectory(@"D:\dir");

        Assert.Throws<IOException>(() => fs.Move(@"D:\a.txt", @"D:\dir"));

        Assert.True(fs.FileExists(@"D:\a.txt"));
    }

    // ---- 빈 폴더 ----

    [Fact]
    public void IsDirectoryEmpty_And_RemoveEmptyDirectory()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a\sub");

        Assert.True(fs.IsDirectoryEmpty(@"D:\a\sub"));
        Assert.False(fs.IsDirectoryEmpty(@"D:\a")); // 하위 폴더가 있다

        fs.RemoveEmptyDirectory(@"D:\a\sub");

        Assert.False(fs.DirectoryExists(@"D:\a\sub"));
        Assert.True(fs.IsDirectoryEmpty(@"D:\a"));
    }

    [Fact]
    public void RemoveEmptyDirectory_Throws_WhenNotEmpty_AndKeepsContent()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a\x.jpg", B("x"));

        Assert.Throws<IOException>(() => fs.RemoveEmptyDirectory(@"D:\a"));

        Assert.True(fs.FileExists(@"D:\a\x.jpg"));
    }

    [Fact]
    public void RemoveEmptyDirectory_Throws_WhenMissing_OrVolumeRoot()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a");

        Assert.Throws<DirectoryNotFoundException>(() => fs.RemoveEmptyDirectory(@"D:\none"));
        fs.RemoveEmptyDirectory(@"D:\a");
        Assert.Throws<IOException>(() => fs.RemoveEmptyDirectory(@"D:\"));
        Assert.True(fs.DirectoryExists(@"D:\"));
    }

    // ---- AppendLine ----

    [Fact]
    public void AppendLine_CreatesFile_AndAppendsInOrder()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\j");

        fs.AppendLine(@"D:\j\log.jsonl", "a");
        fs.AppendLine(@"D:\j\log.jsonl", "b");

        Assert.Equal(B("a" + Environment.NewLine + "b" + Environment.NewLine), fs.ReadAllBytes(@"D:\j\log.jsonl"));
    }

    [Fact]
    public void AppendLines_CreatesFile_AppendsAll_AndCountsOneMutatingOp()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\j");
        fs.AppendLine(@"D:\j\log.jsonl", "a");
        var before = fs.MutatingOps;

        fs.AppendLines(@"D:\j\log.jsonl", ["b", "c", "d"]);
        fs.AppendLines(@"D:\j\new.jsonl", ["x"]);

        Assert.Equal(2, fs.MutatingOps - before);
        var n = Environment.NewLine;
        Assert.Equal(B($"a{n}b{n}c{n}d{n}"), fs.ReadAllBytes(@"D:\j\log.jsonl"));
        Assert.Equal(B($"x{n}"), fs.ReadAllBytes(@"D:\j\new.jsonl"));
    }

    [Fact]
    public void AppendLines_IsAtomic_WhenCrashing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\j");
        fs.CrashAfter(0);

        Assert.Throws<SimulatedCrashException>(() => fs.AppendLines(@"D:\j\log.jsonl", ["a", "b"]));

        Assert.False(fs.FileExists(@"D:\j\log.jsonl"));
    }

    [Fact]
    public void AppendLines_Throws_WhenParentDirectoryMissing()
    {
        var fs = new FakeFileSystem();

        Assert.Throws<DirectoryNotFoundException>(() => fs.AppendLines(@"D:\nope\log.jsonl", ["a"]));
    }

    [Fact]
    public void SetLastWriteTimeUtc_ChangesOnlyTheTime_AndCountsAsMutatingOp()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.jpg", [1, 2, 3]);
        var before = fs.MutatingOps;
        var time = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        fs.SetLastWriteTimeUtc(@"D:\a.jpg", time);

        Assert.Equal(1, fs.MutatingOps - before);
        Assert.Equal(time, fs.GetFile(@"D:\a.jpg").LastWriteTimeUtc);
        Assert.Equal(B("\u0001\u0002\u0003"), fs.ReadAllBytes(@"D:\a.jpg"));
    }

    [Fact]
    public void SetLastWriteTimeUtc_Throws_WhenMissing_OrOpenForWriting()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"D:\");

        Assert.Throws<FileNotFoundException>(() => fs.SetLastWriteTimeUtc(@"D:\none.jpg", DateTime.UtcNow));
        using var writing = fs.CreateNew(@"D:\w.jpg");
        Assert.Throws<IOException>(() => fs.SetLastWriteTimeUtc(@"D:\w.jpg", DateTime.UtcNow));
    }

    [Fact]
    public void AppendLine_Throws_WhenParentDirectoryMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\j");

        Assert.Throws<DirectoryNotFoundException>(() => fs.AppendLine(@"D:\nope\log.jsonl", "a"));
    }

    // ---- 볼륨과 여유 공간 ----

    [Fact]
    public void GetVolumeRoot_ReturnsDriveRoot_ForEachDrive()
    {
        var fs = new FakeFileSystem();

        Assert.Equal(@"C:\", fs.GetVolumeRoot(@"C:\x\y.jpg"));
        Assert.Equal(@"E:\", fs.GetVolumeRoot(@"E:\"));
        Assert.NotEqual(fs.GetVolumeRoot(@"C:\a"), fs.GetVolumeRoot(@"D:\a"));
    }

    [Fact]
    public void FreeSpace_DefaultsToLargeValue_AndSetFreeSpaceIsPerVolume()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\a", B("x"));
        fs.AddFile(@"D:\a", B("x"));

        Assert.Equal(long.MaxValue / 4, fs.GetAvailableFreeSpace(@"C:\a"));

        fs.SetFreeSpace(@"D:\", 100);

        Assert.Equal(100, fs.GetAvailableFreeSpace(@"d:\anything\deep"));
        Assert.Equal(long.MaxValue / 4, fs.GetAvailableFreeSpace(@"C:\a"));
    }

    [Fact]
    public void RemovedVolume_FreeSpaceThrows_ButGetVolumeRootStillWorks()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"E:\a", B("x"));
        fs.RemoveVolume(@"E:\");

        Assert.Throws<DirectoryNotFoundException>(() => fs.GetAvailableFreeSpace(@"E:\a"));
        Assert.Equal(@"E:\", fs.GetVolumeRoot(@"E:\a"));
    }

    // ---- 장애 주입과 계측 ----

    [Fact]
    public void MutatingOps_CountsEachMutation_AndNotReads()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a\x.jpg", B("x"));

        fs.FileExists(@"D:\a\x.jpg");
        fs.GetFile(@"D:\a\x.jpg");
        using (fs.OpenRead(@"D:\a\x.jpg")) { }
        fs.EnumerateFiles(@"D:\a", true).ToList();
        fs.IsDirectoryEmpty(@"D:\a");
        Assert.Equal(0, fs.MutatingOps);

        fs.CreateDirectory(@"D:\b");                      // 1
        fs.Move(@"D:\a\x.jpg", @"D:\b\x.jpg");            // 2
        fs.AppendLine(@"D:\b\log", "l");                  // 3
        Write(fs.CreateNew(@"D:\b\n.bin"), "1", "2");     // 4 (CreateNew) + 5, 6 (Write 두 번)
        fs.RemoveEmptyDirectory(@"D:\a");                 // 7

        Assert.Equal(7, fs.MutatingOps);
    }

    [Fact]
    public void CrashAfter_CountsFromNow_AndKeepsFailingUntilClearFault()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(@"D:\a");
        fs.CrashAfter(1);

        fs.CreateDirectory(@"D:\b");
        Assert.Throws<SimulatedCrashException>(() => fs.CreateDirectory(@"D:\c"));
        Assert.Throws<SimulatedCrashException>(() => fs.CreateDirectory(@"D:\d"));
        Assert.False(fs.DirectoryExists(@"D:\c"));

        fs.ClearFault();
        fs.CreateDirectory(@"D:\c");

        Assert.True(fs.DirectoryExists(@"D:\c"));
    }

    [Fact]
    public void CrashAfter_Zero_CrashesOnFirstMutation()
    {
        var fs = new FakeFileSystem();
        fs.CrashAfter(0);

        Assert.Throws<SimulatedCrashException>(() => fs.CreateDirectory(@"D:\a"));
        Assert.False(fs.DirectoryExists(@"D:\a"));
    }

    [Fact]
    public void ReadsStillWork_AfterCrash()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.jpg", B("x"));
        fs.CrashAfter(0);
        Assert.Throws<SimulatedCrashException>(() => fs.CreateDirectory(@"D:\b"));

        Assert.True(fs.FileExists(@"D:\a.jpg"));
        Assert.Equal(B("x"), fs.ReadAllBytes(@"D:\a.jpg"));
    }

    [Fact]
    public void SimulatedCrashException_IsNotAnIOException()
    {
        Assert.False(typeof(IOException).IsAssignableFrom(typeof(SimulatedCrashException)));
        Assert.True(typeof(Exception).IsAssignableFrom(typeof(SimulatedCrashException)));
    }

    [Fact]
    public void ClearFault_AlsoClearsFailReadsAndCorruptNextWrite()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.jpg", B("x"));
        fs.FailReadsOf(@"D:\a.jpg");
        fs.CorruptNextWrite();

        fs.ClearFault();

        using (fs.OpenRead(@"D:\a.jpg")) { }
        Write(fs.CreateNew(@"D:\b.bin"), "AB");
        Assert.Equal(B("AB"), fs.ReadAllBytes(@"D:\b.bin"));
    }

    // ---- 검사용 보조 ----

    [Fact]
    public void AllFiles_ListsEveryFile_IncludingOnRemovedVolumes()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.jpg", B("1"));
        fs.AddFile(@"E:\b.jpg", B("2"));
        fs.RemoveVolume(@"E:\");

        Assert.Equal(new[] { @"D:\a.jpg", @"E:\b.jpg" }, fs.AllFiles());
        Assert.Equal(B("2"), fs.ReadAllBytes(@"E:\b.jpg"));
    }

    [Fact]
    public void ReadAllBytes_ReturnsCopy_SoCallersCannotMutateTheDisk()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.jpg", B("abc"));

        fs.ReadAllBytes(@"D:\a.jpg")[0] = 0;

        Assert.Equal(B("abc"), fs.ReadAllBytes(@"D:\a.jpg"));
    }

    [Fact]
    public void AddFile_CopiesContent_SoLaterMutationOfCallerArrayDoesNotLeak()
    {
        var fs = new FakeFileSystem();
        var content = B("abc");
        fs.AddFile(@"D:\a.jpg", content);

        content[0] = 0;

        Assert.Equal(B("abc"), fs.ReadAllBytes(@"D:\a.jpg"));
    }

    [Fact]
    public void OpenRead_AllowsConcurrentReaders_ButBlocksAppend()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a.log", B("abc"));

        using var first = fs.OpenRead(@"D:\a.log");
        using var second = fs.OpenRead(@"D:\a.log");

        Assert.Throws<IOException>(() => fs.AppendLine(@"D:\a.log", "x"));
    }
}
