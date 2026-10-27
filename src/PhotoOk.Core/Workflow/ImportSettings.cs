using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;

namespace PhotoOk.Core.Workflow;

/// <summary>가져오기 화면에서 사용자가 고른 값.</summary>
public sealed record ImportSettings(
    IReadOnlyList<string> Sources,
    bool IncludeSubfolders,
    string ArchiveRoot,
    Preset Preset,
    TransferMode Mode);
