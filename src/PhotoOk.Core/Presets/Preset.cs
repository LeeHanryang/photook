namespace PhotoOk.Core.Presets;

public enum BranchCondition { Always, NeedsReview }

public enum EmptyValue { Omit, Replace }

/// <summary>폴더 한 단계의 템플릿. 값이 비었을 때 단계를 생략하거나 <paramref name="Replacement"/>로 바꾼다.</summary>
public sealed record LevelTemplate(string Template, EmptyValue WhenEmpty = EmptyValue.Omit, string? Replacement = null);

public sealed record LayoutBranch(string Name, BranchCondition When, IReadOnlyList<LevelTemplate> Levels);

public sealed record DeviceAlias(string Alias, string? Model = null, string? SerialNumber = null, string? FileNamePrefix = null);

/// <summary>아카이브 정리 방식. 분기는 위에서부터 평가하며 마지막 분기는 항상 <see cref="BranchCondition.Always"/>여야 한다.</summary>
public sealed record Preset(string Name, TimeOnly DayStart, IReadOnlyList<LayoutBranch> Branches,
    IReadOnlyList<DeviceAlias> DeviceAliases, IReadOnlyDictionary<string, IReadOnlyList<string>> FormatGroups);
