using System.IO;
using System.Text.RegularExpressions;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Execution;

public class JournalTests
{
    private const string Archive = @"D:\사진 아카이브";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string RunId = "20261027-103000-ab12";

    private readonly FakeFileSystem _fs = new();
    private readonly string _archivePath = JournalPaths.InArchive(Archive, RunId);
    private readonly string _appDataPath = JournalPaths.InAppData(AppData, RunId);

    private JournalWriter NewWriter() => new(_fs, new[] { _archivePath, _appDataPath });

    private static JournalEntry Entry(JournalEvent ev, string? opId = null, OperationKind? kind = null, string? source = null,
        string? destination = null, long length = 0, string? hash = null) =>
        new(RunId, ev, opId, kind, source, destination, length, hash, null, new DateTime(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc));

    private string[] Lines(string path) =>
        System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(path)).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

    private void WriteStandardRun(JournalWriter writer, bool complete)
    {
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.CreateDirectory, null, @"D:\사진 아카이브\2026", 0));
        writer.Write(Entry(JournalEvent.Planned, "op2", OperationKind.Copy, @"E:\DCIM\a.jpg", @"D:\사진 아카이브\2026\a.jpg", 1234));
        writer.Write(Entry(JournalEvent.Started, "op1"));
        writer.Write(Entry(JournalEvent.Completed, "op1"));
        writer.Write(Entry(JournalEvent.Started, "op2"));
        if (complete)
        {
            writer.Write(Entry(JournalEvent.Completed, "op2", hash: "0123456789abcdef"));
        }
    }

    [Fact]
    public void Paths_UseExpectedLayout()
    {
        Assert.Equal(@"C:\X\journal\r1.jsonl", JournalPaths.InAppData(@"C:\X", "r1"));
        Assert.Equal(@"D:\A\.photook\journal\r1.jsonl", JournalPaths.InArchive(@"D:\A", "r1"));
    }

    [Fact]
    public void NewRunId_HasExpectedFormat()
    {
        var id = JournalPaths.NewRunId(DateTime.UtcNow);
        Assert.Matches(new Regex(@"^\d{8}-\d{6}-[0-9a-f]{4}$"), id);
    }

    [Fact]
    public void NewRunId_UsesLocalTime()
    {
        var utc = new DateTime(2026, 10, 27, 1, 2, 3, DateTimeKind.Utc);
        var expected = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("yyyyMMdd-HHmmss");
        Assert.StartsWith(expected + "-", JournalPaths.NewRunId(utc));
    }

    [Fact]
    public void Write_AppendsToBothPaths()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));

        var a = Lines(_archivePath);
        var b = Lines(_appDataPath);
        Assert.Single(a);
        Assert.Equal(a, b);
        Assert.Empty(writer.Warnings);
        Assert.Contains("사진 아카이브", a[0]);
        Assert.Contains("\"event\":\"RunStarted\"", a[0]);
        Assert.DoesNotContain("\"opId\"", a[0]);
    }

    [Fact]
    public void WriteMany_AppendsAllEntriesToBothPaths_InOneCallEach()
    {
        var hooked = new HookedFileSystem(_fs);
        var writer = new JournalWriter(hooked, new[] { _archivePath, _appDataPath });

        writer.WriteMany([
            Entry(JournalEvent.RunStarted, destination: Archive),
            Entry(JournalEvent.Planned, "op1", OperationKind.CreateDirectory, null, @"D:\사진 아카이브\2026", 0),
            Entry(JournalEvent.Planned, "op2", OperationKind.Copy, @"E:\DCIM\a.jpg", @"D:\사진 아카이브\2026\a.jpg", 1234),
        ]);
        writer.Write(Entry(JournalEvent.Started, "op1"));

        Assert.Equal([(_archivePath, 3), (_appDataPath, 3), (_archivePath, 1), (_appDataPath, 1)], hooked.Appends);
        var a = Lines(_archivePath);
        Assert.Equal(4, a.Length);
        Assert.Equal(a, Lines(_appDataPath));
        var state = JournalReader.Load(_fs, _archivePath);
        Assert.Equal(2, state.Operations.Count);
        Assert.Equal(OpStatus.Started, state.Operations[0].Status);
    }

    [Fact]
    public void WriteMany_ContinuesWithWarning_WhenOnePathFails_AndStopsUsingIt()
    {
        _fs.RemoveVolume(@"C:\");
        var writer = NewWriter();

        writer.WriteMany([Entry(JournalEvent.RunStarted, destination: Archive),
            Entry(JournalEvent.Planned, "op1", OperationKind.CreateDirectory, null, Archive, 0)]);
        _fs.RestoreVolume(@"C:\");
        writer.Write(Entry(JournalEvent.Started, "op1"));

        Assert.Equal(3, Lines(_archivePath).Length);
        Assert.False(_fs.FileExists(_appDataPath));
        Assert.Equal(new[] { $"기록을 남기지 못함: {_appDataPath}" }, writer.Warnings);
        Assert.Equal(new[] { _archivePath }, writer.HealthyPaths);
    }

    [Fact]
    public void WriteMany_Throws_WhenAllPathsFail()
    {
        _fs.RemoveVolume(@"C:\");
        _fs.RemoveVolume(@"D:\");
        var writer = NewWriter();

        Assert.Throws<JournalUnavailableException>(() => writer.WriteMany([Entry(JournalEvent.RunStarted, destination: Archive)]));
        Assert.Equal(2, writer.Warnings.Count);
    }

    [Fact]
    public void WriteMany_StartsNewLine_AfterTornLastLine()
    {
        var first = new JournalWriter(_fs, new[] { _archivePath });
        first.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        _fs.AddFile(_archivePath, [.. _fs.ReadAllBytes(_archivePath), .. System.Text.Encoding.UTF8.GetBytes("{\"runId\":\"x\",\"ev")]);
        var writer = new JournalWriter(_fs, new[] { _archivePath });

        writer.WriteMany([Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5),
            Entry(JournalEvent.Started, "op1")]);

        var state = JournalReader.Load(_fs, _archivePath);
        Assert.Equal(OpStatus.Started, Assert.Single(state.Operations).Status);
    }

    [Fact]
    public void Write_ContinuesWithWarning_WhenOnePathFails()
    {
        _fs.AddDirectory(@"C:\");
        _fs.RemoveVolume(@"C:\");
        var writer = NewWriter();

        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));

        Assert.Single(Lines(_archivePath));
        Assert.Equal(new[] { $"기록을 남기지 못함: {_appDataPath}" }, writer.Warnings);
    }

    [Fact]
    public void Write_DoesNotDuplicateWarning_OnRepeatedFailures()
    {
        _fs.RemoveVolume(@"C:\");
        var writer = NewWriter();

        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.CreateDirectory, null, Archive, 0));
        writer.Write(Entry(JournalEvent.Started, "op1"));

        Assert.Equal(3, Lines(_archivePath).Length);
        Assert.Single(writer.Warnings);
    }

    [Fact]
    public void HealthyPaths_ExcludePathsThatFailed_InOrder()
    {
        var writer = NewWriter();
        Assert.Equal(new[] { _archivePath, _appDataPath }, writer.HealthyPaths);

        _fs.RemoveVolume(@"D:\");
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));

        Assert.Equal(new[] { _appDataPath }, writer.HealthyPaths);
    }

    [Fact]
    public void Write_Throws_WhenAllPathsFail()
    {
        _fs.RemoveVolume(@"C:\");
        _fs.RemoveVolume(@"D:\");
        var writer = NewWriter();

        Assert.Throws<JournalUnavailableException>(() => writer.Write(Entry(JournalEvent.RunStarted, destination: Archive)));
        Assert.Equal(2, writer.Warnings.Count);
    }

    [Fact]
    public void Write_PropagatesSimulatedCrash()
    {
        _fs.CrashAfter(0);
        var writer = NewWriter();

        Assert.Throws<SimulatedCrashException>(() => writer.Write(Entry(JournalEvent.RunStarted, destination: Archive)));
    }

    [Fact]
    public void Load_ReplaysLastEventPerOperation()
    {
        var writer = NewWriter();
        WriteStandardRun(writer, complete: true);

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(RunId, state.RunId);
        Assert.Equal(Archive, state.ArchiveRoot);
        Assert.False(state.Finished);
        Assert.Equal(2, state.Operations.Count);

        var op1 = state.Operations[0];
        Assert.Equal("op1", op1.Operation.Id);
        Assert.Equal(OperationKind.CreateDirectory, op1.Operation.Kind);
        Assert.Null(op1.Operation.Source);
        Assert.Equal(OpStatus.Completed, op1.Status);
        Assert.Equal(5, op1.CompletedSeq);
        Assert.Empty(op1.Operation.Reasons);

        var op2 = state.Operations[1];
        Assert.Equal("op2", op2.Operation.Id);
        Assert.Equal(OperationKind.Copy, op2.Operation.Kind);
        Assert.Equal(@"E:\DCIM\a.jpg", op2.Operation.Source);
        Assert.Equal(@"D:\사진 아카이브\2026\a.jpg", op2.Operation.Destination);
        Assert.Equal(1234, op2.Operation.Length);
        Assert.Equal(OpStatus.Completed, op2.Status);
        Assert.Equal("0123456789abcdef", op2.Hash);
        Assert.Equal(7, op2.CompletedSeq);
    }

    [Fact]
    public void Load_TracksLastSeq_AndUndoStarted()
    {
        var writer = NewWriter();
        WriteStandardRun(writer, complete: false);   // 줄 1~6: op1은 5번째 줄(Completed), op2는 6번째 줄(Started)이 마지막
        var before = JournalReader.Load(_fs, _archivePath);
        Assert.Equal(5, before.Operations[0].LastSeq);
        Assert.Equal(6, before.Operations[1].LastSeq);
        Assert.False(before.UndoStarted);

        writer.Write(Entry(JournalEvent.Failed, "op2"));       // 7
        writer.Write(Entry(JournalEvent.UndoStarted));         // 8: 작업 ID가 없다
        writer.Write(Entry(JournalEvent.Undone, "op1"));       // 9
        var after = JournalReader.Load(_fs, _archivePath);

        Assert.True(after.UndoStarted);
        Assert.Equal(9, after.Operations[0].LastSeq);
        Assert.Equal(5, after.Operations[0].CompletedSeq);
        Assert.Equal(7, after.Operations[1].LastSeq);
        Assert.Equal(9, after.EntryCount);
    }

    [Fact]
    public void Load_ReportsStartedAndFinished()
    {
        var writer = NewWriter();
        WriteStandardRun(writer, complete: false);
        var midRun = JournalReader.Load(_fs, _appDataPath);
        Assert.Equal(OpStatus.Started, midRun.Operations[1].Status);
        Assert.Equal(0, midRun.Operations[1].CompletedSeq);
        Assert.Null(midRun.Operations[1].Hash);

        writer.Write(Entry(JournalEvent.Failed, "op2"));
        writer.Write(Entry(JournalEvent.RunFinished));
        var done = JournalReader.Load(_fs, _appDataPath);
        Assert.Equal(OpStatus.Failed, done.Operations[1].Status);
        Assert.True(done.Finished);
    }

    [Theory]
    [InlineData(JournalEvent.Skipped, OpStatus.Skipped)]
    [InlineData(JournalEvent.Undone, OpStatus.Undone)]
    [InlineData(JournalEvent.UndoSkipped, OpStatus.UndoSkipped)]
    public void Load_MapsTerminalEvents(JournalEvent ev, OpStatus expected)
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));
        writer.Write(Entry(ev, "op1"));

        Assert.Equal(expected, JournalReader.Load(_fs, _archivePath).Operations[0].Status);
    }

    [Fact]
    public void Load_IgnoresTruncatedLastLine()
    {
        var writer = NewWriter();
        WriteStandardRun(writer, complete: true);
        _fs.AppendLine(_archivePath, "{\"runId\":\"x\",\"ev");

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(2, state.Operations.Count);
        Assert.Equal(OpStatus.Completed, state.Operations[1].Status);
    }

    [Fact]
    public void Load_Throws_WhenCorruptLineIsInTheMiddle()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        _fs.AppendLine(_archivePath, "{\"runId\":\"x\",\"event\":}");
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
        Assert.Contains("2번째 줄", ex.Message);
    }

    [Fact]
    public void Load_Throws_OnUnparseableLineEndingWithBrace()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        _fs.AppendLine(_archivePath, "{\"runId\":\"x\",\"event\":}");

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
        Assert.Contains("2번째 줄", ex.Message);
    }

    [Fact]
    public void Load_IgnoresTornFragment_EvenWhenFollowedByMoreLines()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        _fs.AppendLine(_archivePath, "{\"runId\":\"x\",\"ev");
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Single(state.Operations);
    }

    private const string TornCompletedWithBrace =
        "{\"runId\":\"x\",\"event\":\"Completed\",\"opId\":\"op-000001\",\"destination\":\"a}";

    /// <summary>RunStarted, op-000001의 Planned·Started를 정상 줄로 쓰고, 그 뒤에 <paramref name="tail"/>을 그대로 붙인다.</summary>
    private void WriteRunWithRawTail(string tail)
    {
        var writer = new JournalWriter(_fs, new[] { _archivePath });
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        writer.Write(Entry(JournalEvent.Planned, "op-000001", OperationKind.Copy, @"E:\a.jpg", @"D:\사진 아카이브\a.jpg", 5));
        writer.Write(Entry(JournalEvent.Started, "op-000001"));
        var bytes = _fs.ReadAllBytes(_archivePath).Concat(System.Text.Encoding.UTF8.GetBytes(tail)).ToArray();
        _fs.AddFile(_archivePath, bytes);
    }

    [Fact]
    public void Load_IgnoresUnterminatedLastLine_EvenIfItEndsWithBrace()
    {
        WriteRunWithRawTail(TornCompletedWithBrace);

        var state = JournalReader.Load(_fs, _archivePath);

        var op = Assert.Single(state.Operations);
        Assert.Equal("op-000001", op.Operation.Id);
        Assert.NotEqual(OpStatus.Completed, op.Status);
        Assert.Equal(OpStatus.Started, op.Status);
        Assert.Equal(3, state.EntryCount);
    }

    [Fact]
    public void Load_IgnoresUnterminatedLastLine_EvenIfItIsCompleteJson()
    {
        // 줄바꿈까지 쓰기 전에 끊겼다면 그 줄은 기록된 것으로 보지 않는다(내용과 상관없이).
        var complete = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.Completed, "op-000001"), JournalWriter.JsonOptions);
        WriteRunWithRawTail(complete);

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(OpStatus.Started, Assert.Single(state.Operations).Status);
        Assert.Equal(3, state.EntryCount);
    }

    [Fact]
    public void Load_IgnoresUnterminatedLastLine_EndingWithCarriageReturnOnly()
    {
        var complete = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.Completed, "op-000001"), JournalWriter.JsonOptions);
        WriteRunWithRawTail(complete + "\r");

        Assert.Equal(OpStatus.Started, Assert.Single(JournalReader.Load(_fs, _archivePath).Operations).Status);
    }

    [Fact]
    public void Load_StillThrows_OnTerminatedCorruptLineEndingWithBrace()
    {
        WriteRunWithRawTail(TornCompletedWithBrace + "\r\n");

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
        Assert.Contains("4번째 줄", ex.Message);
    }

    private string[] RawLines(string path) =>
        System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(path)).Split("\r\n");

    [Fact]
    public void Write_ClosesTornLine_WithTerminatorAndMarker()
    {
        WriteRunWithRawTail(TornCompletedWithBrace);
        var opsBefore = _fs.MutatingOps;
        var next = Entry(JournalEvent.Completed, "op-000001", hash: "0123456789abcdef");

        new JournalWriter(_fs, new[] { _archivePath }).Write(next);

        var lines = RawLines(_archivePath);
        Assert.Equal(7, lines.Length); // RunStarted, Planned, Started, 조각, 표시, 새 항목, 끝의 빈 문자열
        Assert.Equal(TornCompletedWithBrace, lines[3]);
        var marker = System.Text.Json.JsonSerializer.Deserialize<JournalEntry>(lines[4], JournalWriter.JsonOptions)!;
        Assert.Equal(new JournalEntry(RunId, JournalEvent.TornLineClosed, TimestampUtc: next.TimestampUtc), marker);
        Assert.DoesNotContain("opId", lines[4]);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(next, JournalWriter.JsonOptions), lines[5]);
        Assert.Equal(string.Empty, lines[6]);
        Assert.Equal(opsBefore + 3, _fs.MutatingOps); // 폴더 준비, 줄바꿈+표시(한 번에), 새 항목
    }

    [Fact]
    public void WriteMany_ClosesTornLine_WithMarkerOfFirstEntry()
    {
        WriteRunWithRawTail("{\"runId\":\"x\",\"ev");
        var first = Entry(JournalEvent.Started, "op-000001") with { RunId = "20261027-110000-cd34" };

        new JournalWriter(_fs, new[] { _archivePath }).WriteMany([first, Entry(JournalEvent.Completed, "op-000001")]);

        var marker = System.Text.Json.JsonSerializer.Deserialize<JournalEntry>(RawLines(_archivePath)[4], JournalWriter.JsonOptions)!;
        Assert.Equal(JournalEvent.TornLineClosed, marker.Event);
        Assert.Equal("20261027-110000-cd34", marker.RunId);
    }

    [Fact]
    public void Load_AfterResumeClosedTornBraceLine_StillLoads()
    {
        WriteRunWithRawTail(TornCompletedWithBrace);
        new JournalWriter(_fs, new[] { _archivePath }).Write(Entry(JournalEvent.Completed, "op-000001", hash: "0123456789abcdef"));
        Assert.Contains(RawLines(_archivePath), l => l.Contains("\"event\":\"TornLineClosed\"", StringComparison.Ordinal));

        var first = JournalReader.Load(_fs, _archivePath);

        var op = Assert.Single(first.Operations);
        Assert.Equal(OpStatus.Completed, op.Status);
        Assert.Equal("0123456789abcdef", op.Hash);
        Assert.Equal(4, first.EntryCount); // 조각과 표시는 세지 않는다
        Assert.Equal(0, first.ExpectedOperations); // 시드의 RunStarted 값 그대로(표시가 계획 수를 바꾸지 않는다)

        // 다음 이어 하기·되돌리기도 그대로 읽고 이어 쓴다(이미 줄바꿈으로 끝나므로 표시를 또 쓰지 않는다).
        new JournalWriter(_fs, new[] { _archivePath }).Write(Entry(JournalEvent.UndoStarted));
        var second = JournalReader.Load(_fs, _archivePath);

        Assert.True(second.UndoStarted);
        Assert.Equal(OpStatus.Completed, Assert.Single(second.Operations).Status);
        Assert.Equal(5, second.EntryCount);
        Assert.Single(RawLines(_archivePath), l => l.Contains("TornLineClosed", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_IgnoresClosedTornLine_WhenEarlierClosingWasAlsoCut()
    {
        // 조각을 닫는 중에 다시 끊겨 표시가 반쯤만 쓰였고, 다음 실행이 그 조각을 또 닫았다.
        WriteRunWithRawTail(TornCompletedWithBrace + "\r\n{\"runId\":\"" + RunId + "\",\"event\":\"TornLi");
        new JournalWriter(_fs, new[] { _archivePath }).Write(Entry(JournalEvent.Completed, "op-000001"));

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(OpStatus.Completed, Assert.Single(state.Operations).Status);
        Assert.Equal(4, state.EntryCount);
    }

    [Fact]
    public void Load_KeepsLastEntry_WhenUnterminatedTailIsWhitespaceOnly()
    {
        var complete = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.Completed, "op-000001"), JournalWriter.JsonOptions);
        WriteRunWithRawTail(complete + "\n   ");

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(OpStatus.Completed, Assert.Single(state.Operations).Status);
        Assert.Equal(4, state.EntryCount);
    }

    [Fact]
    public void Load_IgnoresClosedTornLine_WhenMarkerIsTheUnterminatedTail()
    {
        // 조각을 닫는 표시를 다 썼지만 마지막 줄바꿈 직전에 끊겼다. 표시는 기록되지 않은 꼬리지만 조각이 닫혔다는 것은 안다.
        var marker = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.TornLineClosed), JournalWriter.JsonOptions);
        WriteRunWithRawTail(TornCompletedWithBrace + "\r\n" + marker);

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(OpStatus.Started, Assert.Single(state.Operations).Status);
        Assert.Equal(3, state.EntryCount);
    }

    [Fact]
    public void Load_StillThrows_WhenUnterminatedTailIsAnotherEntry()
    {
        var completed = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.Completed, "op-000001"), JournalWriter.JsonOptions);
        WriteRunWithRawTail(TornCompletedWithBrace + "\r\n" + completed);

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
        Assert.Contains("4번째 줄", ex.Message);
    }

    [Fact]
    public void Load_StillThrows_OnBraceLineFollowedByOtherEntry()
    {
        WriteRunWithRawTail(TornCompletedWithBrace + "\r\n");
        new JournalWriter(_fs, new[] { _archivePath }).Write(Entry(JournalEvent.Completed, "op-000001"));

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
        Assert.Contains("4번째 줄", ex.Message);
    }

    [Fact]
    public void Load_StillThrows_WhenMarkerIsNotTheNextEntry()
    {
        var marker = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.TornLineClosed), JournalWriter.JsonOptions);
        var completed = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.Completed, "op-000001"), JournalWriter.JsonOptions);
        WriteRunWithRawTail(TornCompletedWithBrace + "\r\n" + completed + "\r\n" + marker + "\r\n");

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
        Assert.Contains("4번째 줄", ex.Message);
    }

    [Fact]
    public void Load_TreatsMarkerAsNoOp()
    {
        var marker = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.TornLineClosed), JournalWriter.JsonOptions);
        WriteRunWithRawTail(marker + "\r\n");

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(OpStatus.Started, Assert.Single(state.Operations).Status);
        Assert.Equal(3, state.EntryCount);
        Assert.False(state.Finished);
        Assert.False(state.UndoStarted);
    }

    [Fact]
    public void Load_ReadsTerminatedLastLine_AsBefore()
    {
        var complete = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.Completed, "op-000001"), JournalWriter.JsonOptions);
        WriteRunWithRawTail(complete + "\n");

        var state = JournalReader.Load(_fs, _archivePath);

        Assert.Equal(OpStatus.Completed, Assert.Single(state.Operations).Status);
        Assert.Equal(4, state.EntryCount);
    }

    [Fact]
    public void Write_StopsWritingToPath_AfterItFails()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));

        _fs.RemoveVolume(@"D:\");
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));
        _fs.RestoreVolume(@"D:\");
        writer.Write(Entry(JournalEvent.Started, "op1"));

        Assert.Single(Lines(_archivePath));
        Assert.Equal(3, Lines(_appDataPath).Length);
        Assert.Equal(new[] { $"기록을 남기지 못함: {_archivePath}" }, writer.Warnings);
    }

    [Fact]
    public void Write_StartsNewLine_AfterTornLastLine()
    {
        var valid = System.Text.Json.JsonSerializer.Serialize(Entry(JournalEvent.RunStarted, destination: Archive),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
        var seeded = System.Text.Encoding.UTF8.GetBytes(valid + "\n" + "{\"runId\":\"x\",\"ev");
        _fs.AddFile(_archivePath, seeded);
        var writer = new JournalWriter(_fs, new[] { _archivePath });

        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));
        writer.Write(Entry(JournalEvent.Started, "op1"));

        var state = JournalReader.Load(_fs, _archivePath);
        Assert.Single(state.Operations);
        Assert.Equal("op1", state.Operations[0].Operation.Id);
        Assert.Equal(OpStatus.Started, state.Operations[0].Status);
    }

    [Fact]
    public void Write_DoesNotAddBlankLine_WhenFileEndsWithNewline()
    {
        var first = NewWriter();
        first.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        var second = NewWriter();
        second.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));

        Assert.Equal(2, System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(_archivePath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("\n\n", System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(_archivePath)).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Load_Throws_WhenEventReferencesUnknownOperation()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.RunStarted, destination: Archive));
        writer.Write(Entry(JournalEvent.Started, "nope"));

        Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
    }

    [Fact]
    public void Load_Throws_WhenRunStartedMissing()
    {
        var writer = NewWriter();
        writer.Write(Entry(JournalEvent.Planned, "op1", OperationKind.Move, @"E:\a.jpg", @"D:\b.jpg", 5));

        Assert.Throws<InvalidDataException>(() => JournalReader.Load(_fs, _archivePath));
    }
}
