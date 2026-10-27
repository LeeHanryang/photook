namespace PhotoOk.Core.IO;

/// <summary>가져올 곳으로 고를 수 있는 드라이브. <paramref name="Root"/>는 예: <c>E:\</c>.</summary>
public sealed record DriveChoice(string Root, string Label);

public interface IRemovableDrives
{
    IReadOnlyList<DriveChoice> List();
}

/// <summary>연결되어 있고 준비된 이동식 드라이브만 나열한다.</summary>
public sealed class SystemRemovableDrives : IRemovableDrives
{
    public IReadOnlyList<DriveChoice> List()
    {
        var drives = new List<DriveChoice>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Removable && d.IsReady)
                {
                    drives.Add(new DriveChoice(d.RootDirectory.FullName,
                        string.IsNullOrWhiteSpace(d.VolumeLabel) ? "이동식 디스크" : d.VolumeLabel));
                }
            }
            catch (IOException)
            {
                // 목록을 만드는 사이에 뽑힌 드라이브는 건너뛴다.
            }
        }

        return drives;
    }
}
