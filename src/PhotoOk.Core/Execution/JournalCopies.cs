using PhotoOk.Core.IO;

namespace PhotoOk.Core.Execution;

/// <summary>이어 하기와 되돌리기가 함께 쓰는 기록 사본 고르기.</summary>
internal static class JournalCopies
{
    /// <summary>
    /// 같은 실행의 기록 사본([아카이브, 앱 데이터] 순서, 받은 경로가 둘 다 아니면 세 번째 후보) 중 읽어 낸 항목이 가장 많은 것을 고른다.
    /// 같으면 앞 순서. 받은 경로는 반드시 읽을 수 있어야 하고, 다른 사본은 없거나 읽을 수 없으면 후보에서 뺀다.
    /// 고른 사본에만 이어서 써야 한다(뒤처진 사본에 이어 쓰면 빈 곳이 있는 기록이 된다).
    /// </summary>
    public static (string Path, RunState State, bool CopiesDiffer) Choose(IFileSystem fs, string appDataRoot, string journalPath)
    {
        var given = JournalReader.Load(fs, journalPath);
        var candidates = new List<string>
        {
            JournalPaths.InArchive(given.ArchiveRoot, given.RunId),
            JournalPaths.InAppData(appDataRoot, given.RunId),
        };
        if (!candidates.Any(c => SamePath(c, journalPath)))
        {
            candidates.Add(journalPath);
        }

        var loaded = new List<(string Path, RunState State)>();
        var unreadable = false;
        foreach (var candidate in candidates)
        {
            if (SamePath(candidate, journalPath))
            {
                loaded.Add((candidate, given));
                continue;
            }

            if (!fs.FileExists(candidate))
            {
                continue;
            }

            try
            {
                var state = JournalReader.Load(fs, candidate);
                if (string.Equals(state.RunId, given.RunId, StringComparison.Ordinal))
                {
                    loaded.Add((candidate, state));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                unreadable = true;
            }
        }

        var best = loaded[0];
        foreach (var copy in loaded.Skip(1))
        {
            if (copy.State.EntryCount > best.State.EntryCount)
            {
                best = copy;
            }
        }

        var differ = unreadable || loaded.Any(c => c.State.EntryCount != best.State.EntryCount);
        return (best.Path, best.State, differ);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
