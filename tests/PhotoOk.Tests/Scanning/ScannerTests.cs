using System.IO;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Scanning;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Scanning;

public class ScannerTests
{
    private const string Root = @"D:\Source";

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();

    private Scanner CreateScanner(IFileSystem? fileSystem = null) =>
        new(fileSystem ?? _fs, _reader, FileKinds.Default);

    private void Add(string path) => _fs.AddFile(path, [1, 2, 3]);

    private ScanResult Scan(bool recursive = true) => CreateScanner().Scan([Root], recursive);

    private static string Name(PhotoInfo info) => Path.GetFileName(info.Path);

    [Fact]
    public void Scan_AttachesXmpToRaw_NotToJpg()
    {
        Add($@"{Root}\IMG.RW2");
        Add($@"{Root}\IMG.JPG");
        Add($@"{Root}\IMG.xmp");

        var result = Scan();

        Assert.Equal(2, result.Groups.Count);
        var jpg = result.Groups.Single(g => Name(g.Primary) == "IMG.JPG");
        var raw = result.Groups.Single(g => Name(g.Primary) == "IMG.RW2");
        Assert.Empty(jpg.Companions);
        Assert.Equal(["IMG.xmp"], raw.Companions.Select(Name));
    }

    [Fact]
    public void Scan_AttachesDoubleExtensionSidecar()
    {
        Add($@"{Root}\IMG.RW2");
        Add($@"{Root}\IMG.JPG");
        Add($@"{Root}\IMG.RW2.xmp");

        var result = Scan();

        Assert.Equal(2, result.Groups.Count);
        var raw = result.Groups.Single(g => Name(g.Primary) == "IMG.RW2");
        Assert.Equal(["IMG.RW2.xmp"], raw.Companions.Select(Name));
        Assert.Empty(result.Groups.Single(g => Name(g.Primary) == "IMG.JPG").Companions);
    }

    [Fact]
    public void Scan_DoubleExtensionSidecar_GoesToNamedFile_EvenWhenRawWouldBeDefault()
    {
        Add($@"{Root}\IMG.RW2");
        Add($@"{Root}\IMG.JPG");
        Add($@"{Root}\IMG.JPG.xmp");

        var result = Scan();

        var jpg = result.Groups.Single(g => Name(g.Primary) == "IMG.JPG");
        Assert.Equal(["IMG.JPG.xmp"], jpg.Companions.Select(Name));
        Assert.Empty(result.Groups.Single(g => Name(g.Primary) == "IMG.RW2").Companions);
    }

    [Fact]
    public void Scan_SidecarWithMissingPairFile_FallsBackToSameKeyPhoto()
    {
        Add($@"{Root}\IMG.JPG");
        Add($@"{Root}\IMG.RW2.xmp");

        var result = Scan();

        var group = Assert.Single(result.Groups);
        Assert.Equal("IMG.JPG", Name(group.Primary));
        Assert.Equal(["IMG.RW2.xmp"], group.Companions.Select(Name));
    }

    [Fact]
    public void Scan_PairsLivePhotoMov_WithHeic()
    {
        Add($@"{Root}\IMG_1.HEIC");
        Add($@"{Root}\IMG_1.MOV");

        var result = Scan();

        var group = Assert.Single(result.Groups);
        Assert.Equal("IMG_1.HEIC", Name(group.Primary));
        Assert.Equal(["IMG_1.MOV"], group.Companions.Select(Name));
    }

    [Fact]
    public void Scan_VideoWithoutImage_IsOwnGroup_AndRawDoesNotAbsorbIt()
    {
        Add($@"{Root}\A.MP4");
        Add($@"{Root}\B.RAF");
        Add($@"{Root}\B.MOV");

        var result = Scan();

        Assert.Equal(3, result.Groups.Count);
        Assert.All(result.Groups, g => Assert.Empty(g.Companions));
    }

    [Fact]
    public void Scan_SidecarOfLivePhotoVideo_FollowsVideoToImageGroup()
    {
        Add($@"{Root}\IMG_1.HEIC");
        Add($@"{Root}\IMG_1.MOV");
        Add($@"{Root}\IMG_1.MOV.xmp");

        var result = Scan();

        var group = Assert.Single(result.Groups);
        Assert.Equal(["IMG_1.MOV", "IMG_1.MOV.xmp"], group.Companions.Select(Name).Order());
    }

    [Fact]
    public void Scan_OrphanSidecar_BecomesOwnGroup()
    {
        Add($@"{Root}\X.xmp");

        var result = Scan();

        var group = Assert.Single(result.Groups);
        Assert.Equal(MediaKind.Sidecar, group.Primary.Kind);
        Assert.Empty(group.Companions);
        Assert.Equal("X", group.Key);
    }

    [Fact]
    public void Scan_OrphanDoubleExtensionSidecar_KeyDropsBothExtensions()
    {
        Add($@"{Root}\IMG.RW2.xmp");

        var group = Assert.Single(Scan().Groups);

        Assert.Equal("IMG", group.Key);
    }

    [Fact]
    public void Scan_BorrowsCaptureTime_FromSameNameSibling()
    {
        var taken = new DateTime(2026, 10, 14, 10, 0, 0);
        Add($@"{Root}\DSCF1.RAF");
        Add($@"{Root}\DSCF1.JPG");
        _reader.Set($@"{Root}\DSCF1.JPG", taken, "FUJIFILM", "X-T5");

        var result = Scan();

        var raf = result.Groups.Single(g => Name(g.Primary) == "DSCF1.RAF");
        Assert.Equal(taken, raf.Primary.CaptureTime);
        Assert.Null(raf.Primary.CameraMake);
        Assert.Equal(taken, result.Groups.Single(g => Name(g.Primary) == "DSCF1.JPG").Primary.CaptureTime);
    }

    [Fact]
    public void Scan_DoesNotBorrowCaptureTime_AcrossFolders_OrFromDifferentName()
    {
        var taken = new DateTime(2026, 10, 14, 10, 0, 0);
        Add($@"{Root}\A\DSCF1.RAF");
        Add($@"{Root}\B\DSCF1.JPG");
        Add($@"{Root}\A\DSCF2.JPG");
        _reader.Set($@"{Root}\B\DSCF1.JPG", taken);
        _reader.Set($@"{Root}\A\DSCF2.JPG", taken);

        var result = Scan();

        Assert.Null(result.Groups.Single(g => Name(g.Primary) == "DSCF1.RAF").Primary.CaptureTime);
    }

    [Fact]
    public void Scan_OrphanSidecar_NeverBorrowsCaptureTime()
    {
        Add($@"{Root}\IMG.xmp");
        Add($@"{Root}\Other\IMG.JPG");
        _reader.Set($@"{Root}\Other\IMG.JPG", new DateTime(2026, 1, 1));

        var result = Scan();

        Assert.Null(result.Groups.Single(g => g.Primary.Kind == MediaKind.Sidecar).Primary.CaptureTime);
    }

    [Fact]
    public void Scan_SkipsPhotookFolderAndTempFiles()
    {
        Add($@"{Root}\IMG.JPG");
        Add($@"{Root}\.photook\journal\run.jpg");
        Add($@"{Root}\sub\.PHOTOOK\되돌림\old.jpg");
        Add($@"{Root}\IMG.JPG.photook-tmp");
        Add($@"{Root}\IMG.JPG.photook-tmp-20261027-101500-ab12");
        Add($@"{Root}\IMG.xmp.photook-tmp-20261027-101500-ab12");

        var result = Scan();

        var group = Assert.Single(result.Groups);
        Assert.Equal("IMG.JPG", Name(group.Primary));
        Assert.Empty(result.Ignored);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Scan_PutsOtherFilesInIgnored()
    {
        Add($@"{Root}\IMG.JPG");
        Add($@"{Root}\Thumbs.db");

        var result = Scan();

        Assert.Equal([$@"{Root}\Thumbs.db"], result.Ignored);
        Assert.Single(result.Groups);
    }

    [Fact]
    public void Scan_NonRecursive_IgnoresSubfolders()
    {
        Add($@"{Root}\TOP.JPG");
        Add($@"{Root}\sub\DEEP.JPG");

        Assert.Equal(["TOP.JPG"], Scan(recursive: false).Groups.Select(g => Name(g.Primary)));
        Assert.Equal(["DEEP.JPG", "TOP.JPG"], Scan(recursive: true).Groups.Select(g => Name(g.Primary)).Order());
    }

    [Fact]
    public void Scan_SameNameInDifferentFolders_StaysSeparate()
    {
        Add($@"{Root}\A\IMG.RW2");
        Add($@"{Root}\B\IMG.xmp");

        var result = Scan();

        Assert.Equal(2, result.Groups.Count);
        Assert.All(result.Groups, g => Assert.Empty(g.Companions));
    }

    [Fact]
    public void Scan_MatchesNamesCaseInsensitively()
    {
        Add($@"{Root}\img_1.heic");
        Add($@"{Root}\IMG_1.mov");
        Add($@"{Root}\Img_1.XMP");

        var group = Assert.Single(Scan().Groups);

        Assert.Equal(2, group.Companions.Count);
    }

    [Fact]
    public void Scan_GroupsAreOrderedByPrimaryPath_AndAllFilesListsPrimaryFirst()
    {
        Add($@"{Root}\b.JPG");
        Add($@"{Root}\A\z.JPG");
        Add($@"{Root}\a.JPG");
        Add($@"{Root}\a.xmp");

        var result = Scan();

        Assert.Equal([$@"{Root}\a.JPG", $@"{Root}\A\z.JPG", $@"{Root}\b.JPG"], result.Groups.Select(g => g.Id));
        Assert.Equal(["a.JPG", "a.xmp"], result.Groups[0].AllFiles.Select(Name));
        Assert.Equal("a", result.Groups[0].Key);
    }

    [Fact]
    public void Scan_OverlappingRoots_DoNotDuplicateFiles()
    {
        Add($@"{Root}\sub\IMG.JPG");

        var result = CreateScanner().Scan([Root, $@"{Root}\sub"], recursive: true);

        Assert.Single(result.Groups);
    }

    [Fact]
    public void Scan_MissingRoot_IsReportedAndOtherRootsStillScanned()
    {
        Add($@"{Root}\IMG.JPG");

        var result = CreateScanner().Scan([@"D:\Nowhere", Root], recursive: true);

        Assert.Single(result.Groups);
        Assert.Equal(@"D:\Nowhere", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void Scan_ReaderThrowsForOneFile_RecordsErrorAndKeepsOthers()
    {
        Add($@"{Root}\GOOD.JPG");
        Add($@"{Root}\BAD.JPG");
        Add($@"{Root}\GOOD.xmp");
        _reader.FailOn($@"{Root}\BAD.JPG", new UnauthorizedAccessException("접근 거부"));

        var result = Scan();

        var error = Assert.Single(result.Errors);
        Assert.Equal($@"{Root}\BAD.JPG", error.Path);
        Assert.Contains("접근 거부", error.Message);
        var group = Assert.Single(result.Groups);
        Assert.Equal("GOOD.JPG", Name(group.Primary));
        Assert.Equal(["GOOD.xmp"], group.Companions.Select(Name));
    }

    [Fact]
    public void Scan_FileVanishesBeforeRead_RecordsError()
    {
        Add($@"{Root}\GOOD.JPG");
        Add($@"{Root}\GONE.JPG");
        var scanner = CreateScanner(new VanishingFileSystem(_fs, $@"{Root}\GONE.JPG"));

        var result = scanner.Scan([Root], recursive: true);

        Assert.Equal($@"{Root}\GONE.JPG", Assert.Single(result.Errors).Path);
        Assert.Equal("GOOD.JPG", Name(Assert.Single(result.Groups).Primary));
    }

    [Fact]
    public void Scan_EnumerationFailsPartWay_KeepsFilesSeenSoFar_AndRecordsRootError()
    {
        Add($@"{Root}\A.JPG");
        Add($@"{Root}\B.JPG");
        Add($@"{Root}\C.JPG");
        var scanner = CreateScanner(new FailingEnumerationFileSystem(_fs, filesBeforeFailure: 2));

        var result = scanner.Scan([Root], recursive: true);

        Assert.Equal(["A.JPG", "B.JPG"], result.Groups.Select(g => Name(g.Primary)));
        Assert.Equal(Root, Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void Scan_CancelledToken_ThrowsOperationCanceled()
    {
        Add($@"{Root}\IMG.JPG");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => CreateScanner().Scan([Root], recursive: true, cts.Token));
    }

    [Fact]
    public void Scan_CancelledWhileReading_PropagatesInsteadOfBecomingScanError()
    {
        Add($@"{Root}\A.JPG");
        Add($@"{Root}\B.JPG");
        _reader.FailOn($@"{Root}\A.JPG", new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => CreateScanner().Scan([Root], recursive: true));
    }

    /// <summary>GetFile이 특정 파일에서만 FileNotFoundException을 던진다.</summary>
    private sealed class VanishingFileSystem(FakeFileSystem inner, string vanishing) : DelegatingFileSystem(inner)
    {
        public override FileEntry GetFile(string path) =>
            string.Equals(path, vanishing, StringComparison.OrdinalIgnoreCase)
                ? throw new FileNotFoundException("사라진 파일", path)
                : base.GetFile(path);
    }

    /// <summary>열거 도중 접근 거부 폴더를 만난 것처럼, 지정한 개수만큼 돌려준 뒤 예외를 던진다.</summary>
    private sealed class FailingEnumerationFileSystem(FakeFileSystem inner, int filesBeforeFailure) : DelegatingFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateFiles(string directory, bool recursive)
        {
            foreach (var path in base.EnumerateFiles(directory, recursive).Take(filesBeforeFailure))
            {
                yield return path;
            }

            throw new UnauthorizedAccessException("System Volume Information 접근 거부");
        }
    }

    private abstract class DelegatingFileSystem(IFileSystem inner) : IFileSystem
    {
        public virtual IEnumerable<string> EnumerateFiles(string directory, bool recursive) => inner.EnumerateFiles(directory, recursive);

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public virtual FileEntry GetFile(string path) => inner.GetFile(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public Stream CreateNew(string path) => inner.CreateNew(path);

        public void Move(string source, string destination) => inner.Move(source, destination);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public bool IsDirectoryEmpty(string path) => inner.IsDirectoryEmpty(path);

        public void RemoveEmptyDirectory(string path) => inner.RemoveEmptyDirectory(path);

        public void AppendLine(string path, string line) => inner.AppendLine(path, line);

        public string GetVolumeRoot(string path) => inner.GetVolumeRoot(path);

        public long GetAvailableFreeSpace(string path) => inner.GetAvailableFreeSpace(path);
    }
}
