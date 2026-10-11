using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MRemoteMac.Services;
using MRemoteMac.Views;

namespace MRemoteMac;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            NativeMenu.SetMenu(this, BuildAppMenu(main));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>macOS application menu (the "mRemoteMac" menu left of File).</summary>
    private static NativeMenu BuildAppMenu(MainWindow main)
    {
        var menu = new NativeMenu();
        var about = new NativeMenuItem("About mRemoteMac");
        about.Click += (_, _) => AsyncHelper.Fire(() => Dialogs.Dialogs.About(main), "About");
        menu.Add(about);
        menu.Add(new NativeMenuItemSeparator());
        var options = new NativeMenuItem("Options…") { Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta) };
        options.Click += (_, _) => main.ShowOptions();
        menu.Add(options);
        return menu;
    }
}
