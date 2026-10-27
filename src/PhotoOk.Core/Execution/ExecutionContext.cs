using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Execution;

/// <summary>실행기가 쓰는 바깥 의존성. <paramref name="AppDataRoot"/>는 <c>%LocalAppData%\PhotoOk</c>.</summary>
public sealed record ExecutionContext(IFileSystem FileSystem, IHasher Hasher, string AppDataRoot, Func<DateTime> UtcNow);
