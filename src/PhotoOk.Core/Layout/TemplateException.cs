namespace PhotoOk.Core.Layout;

/// <summary>폴더 템플릿 문법이 틀렸을 때 던진다. 메시지는 사용자에게 그대로 보여 줄 수 있다.</summary>
public sealed class TemplateException(string message) : Exception(message);
