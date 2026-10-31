using System.Text;

namespace PhotoOk.Core.Naming;

/// <summary>단어 표·제외 단어·기기 이름 비교가 함께 쓰는 정규화.</summary>
internal static class FolderNameNormalizer
{
    /// <summary>공백·<c>-</c>·<c>_</c>를 지우고 소문자로 만든다. 한글 조합 방식 차이(NFD)는 합쳐서 본다.</summary>
    public static string Normalize(string text)
    {
        try
        {
            text = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // 짝이 없는 서로게이트가 든 이름은 그대로 쓴다.
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && c != '-' && c != '_')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>정규화한 값의 끝에 붙은 숫자를 뗀다(사진1, Camera 01). 전부 숫자면 빈 글자가 된다.</summary>
    public static string TrimTrailingDigits(string normalized) =>
        normalized.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
}
