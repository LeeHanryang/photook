# 포텩 (photook)

사진을 촬영 단위로 정리하는 Windows 프로그램입니다. 원본 파일을 지우거나 고치지 않습니다.

## 빌드

[.NET 10 SDK](https://dotnet.microsoft.com/download)가 필요합니다.

```powershell
dotnet build
dotnet test
dotnet run --project src/PhotoOk
```

## 사용한 오픈소스

| 이름 | 용도 | 라이선스 |
|---|---|---|
| MetadataExtractor | 사진·동영상 메타데이터 읽기 | Apache-2.0 |
| System.IO.Hashing | 파일 검증용 xxHash64 | MIT |

## 라이선스

[MIT](LICENSE)
