using System.Globalization;
using System.Text;

namespace PhotoOk.Core.Layout;

/// <summary>템플릿의 한 조각. 글자(<paramref name="Literal"/>)이거나 토큰(<paramref name="Token"/>, 선택적 <paramref name="Format"/>)이다.</summary>
public sealed record TemplatePart(string? Literal, string? Token, string? Format);

/// <summary>
/// 폴더 한 단계의 템플릿. 문법은 <c>{토큰}</c> 또는 <c>{토큰:서식}</c>이고,
/// <c>{{</c>와 <c>}}</c>는 중괄호 글자 자체다. 서식은 날짜 토큰에만 쓸 수 있다.
/// </summary>
public sealed class PathTemplate
{
    private static readonly HashSet<string> DateTokenNames = new(StringComparer.Ordinal) { "연도", "월", "일", "날짜" };

    public static IReadOnlySet<string> KnownTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "연도", "월", "일", "날짜", "기간", "기기", "제조사", "모델", "포맷", "확장자", "원래폴더", "사유",
    };

    private PathTemplate(IReadOnlyList<TemplatePart> parts) => Parts = parts;

    public IReadOnlyList<TemplatePart> Parts { get; }

    public IEnumerable<string> Tokens => Parts.Where(p => p.Token is not null).Select(p => p.Token!);

    public static PathTemplate Parse(string template)
    {
        var parts = new List<TemplatePart>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                parts.Add(new TemplatePart(literal.ToString(), null, null));
                literal.Clear();
            }
        }

        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i += 2;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new TemplateException($"닫는 중괄호가 없습니다: {template}");
                }

                FlushLiteral();
                parts.Add(ParseToken(template[(i + 1)..close]));
                i = close + 1;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }

                throw new TemplateException($"짝이 없는 닫는 중괄호입니다: {template}");
            }
            else
            {
                literal.Append(c);
                i++;
            }
        }

        FlushLiteral();
        return new PathTemplate(parts);
    }

    private static TemplatePart ParseToken(string body)
    {
        var colon = body.IndexOf(':');
        var name = colon < 0 ? body : body[..colon];
        var format = colon < 0 ? null : body[(colon + 1)..];

        if (!KnownTokens.Contains(name))
        {
            throw new TemplateException($"알 수 없는 토큰: {{{name}}}");
        }

        if (format is not null)
        {
            if (!DateTokenNames.Contains(name))
            {
                throw new TemplateException($"서식은 날짜 토큰에만 쓸 수 있습니다: {{{body}}}");
            }

            try
            {
                if (format.Length == 0)
                {
                    throw new FormatException();
                }

                _ = new DateOnly(2000, 1, 2).ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new TemplateException($"날짜 서식이 올바르지 않습니다: {{{body}}}");
            }
        }

        return new TemplatePart(null, name, format);
    }
}
