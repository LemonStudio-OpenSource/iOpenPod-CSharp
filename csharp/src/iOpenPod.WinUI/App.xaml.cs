using IOpenPod.App.Logging;
using Microsoft.UI.Xaml;

namespace IOpenPod.WinUI;

/// <summary>应用入口。</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>主窗口。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>主窗口的原生句柄，供文件选择器等 WinRT API 使用。</summary>
    public static IntPtr WindowHandle { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Initialize();
        AppLog.Info("应用", "iOpenPod 管理器正在启动。");

        try
        {
            _window = new MainWindow();
            MainWindow = _window;
            WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _window.Activate();
        }
        catch (Exception error)
        {
            AppLog.Error("应用", "主窗口创建失败", error);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("应用", "发生未处理的界面异常", e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception error)
        {
            AppLog.Error("应用", "发生未处理的异常", error);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("应用", "发生未观察的任务异常", e.Exception);
        e.SetObserved();
    }
}
