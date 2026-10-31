using System.IO;

namespace PhotoOk.Tests.Installer;

/// <summary>재배포 의무: Apache-2.0은 라이선스 사본을, BSD는 저작권 고지·조건·면책 문구를 함께 준다.</summary>
public class ThirdPartyNoticesTests
{
    private static readonly string Notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.txt"));

    [Theory]
    [InlineData("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION")]
    [InlineData("4. Redistribution.")]
    [InlineData("9. Accepting Warranty or Additional Liability.")]
    [InlineData("END OF TERMS AND CONDITIONS")]
    public void Apache20_FullText(string part)
    {
        Assert.Contains(part, Notices, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Copyright 2002-2019 Drew Noakes and contributors")]
    [InlineData("Copyright 2006 Adobe Systems Incorporated")]
    [InlineData("Copyright (c) 2020, Adobe")]
    [InlineData("Redistributions of source code must retain the above copyright notice")]
    [InlineData("Redistributions in binary form must reproduce the above copyright notice")]
    [InlineData("Neither the name of the copyright holder nor the names of its")]
    [InlineData("THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\"")]
    public void Copyrights_AndBsdConditions(string part)
    {
        Assert.Contains(part, Notices, StringComparison.Ordinal);
    }

    [Fact]
    public void XmpCore_SaysWhereTheWordingComesFrom_AndThatItMayDiffer()
    {
        Assert.Contains("프로젝트 설정(csproj)", Notices, StringComparison.Ordinal);
        Assert.DoesNotContain("패키지 표기", Notices, StringComparison.Ordinal);
        Assert.Contains("라이선스 링크는 읽을 수 없", Notices, StringComparison.Ordinal);
        Assert.Contains("문구와 연도는 XmpCore가 따른 것과 다를 수 있습니다", Notices, StringComparison.Ordinal);
    }

    [Fact]
    public void Mit_StaysForTheOthers()
    {
        Assert.Contains("Permission is hereby granted, free of charge", Notices, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoOk.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("레포 루트를 찾지 못했습니다.");
    }
}
