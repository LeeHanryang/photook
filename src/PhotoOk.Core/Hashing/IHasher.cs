namespace PhotoOk.Core.Hashing;

/// <summary>파일 내용의 해시. 결과는 소문자 16자리 16진수 문자열이다.</summary>
public interface IHasher
{
    string Hash(string path);
}
