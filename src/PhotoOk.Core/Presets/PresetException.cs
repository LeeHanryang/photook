namespace PhotoOk.Core.Presets;

/// <summary>프리셋을 읽거나 검증하다 실패했을 때 던진다. 메시지는 사용자에게 그대로 보여 줄 수 있다.</summary>
public sealed class PresetException(string message) : Exception(message);
