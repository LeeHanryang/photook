using PhotoOk.Core.Hashing;
using PhotoOk.Core.History;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;
using PhotoOk.Services;
using CoreExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk;

/// <summary>
/// 앱 전체에서 하나씩 쓰는 엔진 부품과 서비스. App이 시작할 때 만들고, 화면 팩토리를 조립할 때 꺼내 쓴다.
/// 가져오기 한 번에 쓰는 <c>ImportWorkflow</c>(해시 캐시를 가짐)는 여기 두지 않고 가져오기를 시작할 때마다 새로 만든다.
/// </summary>
internal sealed record AppServices(
    AppPaths Paths,
    IFileSystem FileSystem,
    IMetadataReader Reader,
    IHasher Hasher,
    CoreExecutionContext Execution,
    PresetLibrary Presets,
    RunHistory History,
    IRemovableDrives Drives,
    IFolderPicker FolderPicker,
    IFileOpenPicker FilePicker,
    IDialogService Dialogs,
    IThumbnailProvider Thumbnails,
    FileLog Log);
