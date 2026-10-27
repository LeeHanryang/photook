using PhotoOk.Core.IO;

namespace PhotoOk.Core.Metadata;

public interface IMetadataReader
{
    /// <summary>파일을 분류하고 촬영 메타데이터를 읽는다. 읽을 수 없어도 예외를 던지지 않는다.</summary>
    PhotoInfo Read(FileEntry file);
}
