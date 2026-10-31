# PhotoOK 설치 파일을 만든다: .NET 런타임을 함께 넣어 게시(self-contained, 폴더 게시)한 뒤 Inno Setup으로 묶는다.
# 결과: installer\output\PhotoOK-Setup-<버전>.exe
# 버전은 저장소 루트의 Directory.Build.props 한 곳에서 읽는다 (앱과 설치 파일이 같은 값을 쓴다).
# 필요: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $PSScriptRoot 'publish'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Encoding UTF8
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Directory.Build.props에서 버전을 찾지 못했습니다.' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6을 찾지 못했습니다: winget install JRSoftware.InnoSetup' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\PhotoOk\PhotoOk.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "게시 실패 ($LASTEXITCODE)" }

& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot 'PhotoOk.iss')
if ($LASTEXITCODE -ne 0) { throw "설치 파일 만들기 실패 ($LASTEXITCODE)" }
Get-Item (Join-Path $PSScriptRoot "output\PhotoOK-Setup-$version.exe")
