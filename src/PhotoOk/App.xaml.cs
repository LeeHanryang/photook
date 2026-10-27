using System.Windows;
using System.Windows.Threading;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.History;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Settings;
using PhotoOk.Services;
using PhotoOk.ViewModels;
using CoreExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk;

/// <summary>
/// 조립 지점. 엔진 부품과 서비스의 실제 구현을 만들고 화면을 연결한다.
/// 처리하지 못한 오류는 로그에 남기고 한국어 안내만 보여 준다(예외 원문은 로그에만).
/// </summary>
public partial class App : Application
{
    private const string ErrorTitle = "문제가 생겼습니다";
    private const string ErrorMessage = "예기치 못한 오류가 생겨 작업을 멈췄습니다. 자세한 내용은 로그 폴더에 남겼습니다.";
    private const string StartFailedMessage = "PhotoOK를 시작하지 못했습니다. 자세한 내용은 로그 폴더에 남겼습니다.";

    private readonly IDialogService _dialogs = new WpfDialogService();
    private FileLog? _log;
    private bool _showingError;

    internal AppServices? Services { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            Services = Compose();
            var shell = new ShellViewModel(Services.Dialogs);
            shell.Navigate(CreateStartScreen(Services, shell));

            var window = new MainWindow(shell);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _log?.Error("앱을 시작하지 못했습니다", ex);
            TryShowError(StartFailedMessage);
            Shutdown(1);
        }
    }

    private AppServices Compose()
    {
        var paths = AppPaths.ForCurrentUser();
        IFileSystem fileSystem = new PhysicalFileSystem();
        _log = new FileLog(fileSystem, paths.LogFolder);

        IHasher hasher = new XxHasher(fileSystem);
        return new AppServices(
            Paths: paths,
            FileSystem: fileSystem,
            Reader: new MetadataExtractorReader(fileSystem, FileKinds.Default, TimeZoneInfo.Local),
            Hasher: hasher,
            Execution: new CoreExecutionContext(fileSystem, hasher, paths.AppDataRoot, () => DateTime.UtcNow),
            Presets: new PresetLibrary(fileSystem, paths.PresetFolder),
            History: new RunHistory(fileSystem, paths.AppDataRoot),
            Drives: new SystemRemovableDrives(),
            FolderPicker: new WpfFolderPicker(),
            FilePicker: new WpfFileOpenPicker(),
            Dialogs: _dialogs,
            Thumbnails: new ShellThumbnailProvider(),
            Log: _log);
    }

    /// <summary>시작 화면. 가져오기를 시작하면 가져오기 화면 팩토리로 화면을 만든다.</summary>
    private static StartViewModel CreateStartScreen(AppServices services, ShellViewModel shell) =>
        new(shell, services.Drives, services.History, driveRoot => CreateSourceScreen(services, shell, driveRoot));

    /// <summary>가져오기 화면. 마지막 선택은 화면을 만들 때마다 다시 읽는다(앞서 저장한 값이 보이도록).</summary>
    private static SourceViewModel CreateSourceScreen(AppServices services, ShellViewModel shell, string? driveRoot) =>
        new(shell, services.FileSystem, services.Presets, services.Paths.SettingsPath,
            AppSettingsStore.Load(services.FileSystem, services.Paths.SettingsPath), driveRoot,
            services.FolderPicker, services.FilePicker, services.Dialogs,
            createScan: _ => new ScanPlaceholderViewModel(), // 스캔 화면이 생기면 여기서 만든다.
            createStart: () => CreateStartScreen(services, shell));

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("화면 스레드에서 처리하지 못한 오류", e.Exception);
        TryShowError(ErrorMessage);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("관찰되지 않은 작업 오류", e.Exception);
        e.SetObserved();

        // 종료자 스레드에서 불리므로 안내 창은 화면 스레드에서 띄운다.
        Dispatcher.BeginInvoke(new Action(() => TryShowError(ErrorMessage)));
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // 프로세스가 끝나는 오류라 되돌릴 수 없다. 로그만 남긴다.
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "알 수 없는 오류");
        _log?.Error("처리하지 못한 오류로 앱이 끝납니다", exception);
    }

    /// <summary>안내 창을 띄운다. 창이 떠 있는 동안 또 오류가 나면 겹쳐 띄우지 않고 로그만 남긴다.</summary>
    private void TryShowError(string message)
    {
        if (_showingError)
        {
            return;
        }

        _showingError = true;
        try
        {
            _dialogs.ShowError(ErrorTitle, message);
        }
        catch (Exception ex)
        {
            _log?.Error("오류 안내 창을 띄우지 못했습니다", ex);
        }
        finally
        {
            _showingError = false;
        }
    }
}
