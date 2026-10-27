namespace PhotoOk.Core.Layout;

public static class PathSanitizer
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 폴더 이름 한 단계를 Windows가 받아들이는 이름으로 바꾼다. 쓸 수 없는 글자는 <c>_</c>로,
    /// 앞뒤 공백과 끝의 점은 없애고, 예약된 장치 이름이면 끝에 <c>_</c>를 붙인다. 결과가 비면 <c>_</c>.
    /// </summary>
    public static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        var text = new string(chars);
        string previous;
        do
        {
            previous = text;
            text = text.Trim().TrimEnd('.');
        }
        while (text != previous);

        if (text.Length == 0)
        {
            return "_";
        }

        var dot = text.IndexOf('.');
        var stem = (dot < 0 ? text : text[..dot]).TrimEnd();
        return ReservedNames.Contains(stem) ? text + "_" : text;
    }
}
