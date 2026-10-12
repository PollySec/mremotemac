#if DEBUG
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MRemoteMac.Models;
using MRemoteMac.ViewModels;
using MRemoteMac.Views;

namespace MRemoteMac.Services;

/// <summary>
/// Script-driven UI automation used for audits (MRM_SCRIPT=file MRM_OUT=dir). Injects raw pointer/keyboard
/// events through Avalonia's InputManager so real hit-testing, focus and gestures are exercised.
/// </summary>
public sealed class UiTestRunner : IDisposable
{
    private readonly MainWindow _main;
    private readonly string _outDir;
    private readonly StreamWriter _log;
    private readonly RawInput _raw = new();
    private int _pass, _fail;

    private UiTestRunner(MainWindow main, string outDir)
    {
        _main = main;
        _outDir = outDir;
        Directory.CreateDirectory(outDir);
        _log = new StreamWriter(Path.Combine(outDir, "audit.log"), false) { AutoFlush = true };
    }

    public static async Task Run(MainWindow main, string scriptPath, string outDir)
    {
        var r = new UiTestRunner(main, outDir);
        var lines = await File.ReadAllLinesAsync(scriptPath);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            r.Log($"> {line}");
            try { await r.Execute(line); }
            catch (Exception ex) { r.Log($"!! {ex.GetType().Name}: {ex.Message}"); r._fail++; }
        }
        r.Log($"== done: {r._pass} passed, {r._fail} failed");
        r.Dispose();
    }

    public void Dispose() => _log.Dispose();

    private void Log(string s)
    {
        _log.WriteLine(s);
        Console.Error.WriteLine("[ui] " + s);
    }

    private static IEnumerable<Window> Windows()
        => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.Where(w => w.IsVisible);

    private static Window Topmost() => Windows().Last();

    private async Task Execute(string line)
    {
        var sp = line.IndexOf(' ');
        var cmd = sp < 0 ? line : line[..sp];
        var arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
        switch (cmd)
        {
            case "wait": await Task.Delay(int.Parse(arg)); break;
            case "shot": await Shot(arg); break;
            case "click": await Click(arg, MouseButton.Left, 1); break;
            case "rclick": await Click(arg, MouseButton.Right, 1); break;
            case "dblclick": await Click(arg, MouseButton.Left, 2); break;
            case "move": await Move(arg); break;
            case "key": await Key(arg); break;
            case "type": await TypeText(arg); break;
            case "cell": await Cell(arg); break;
            case "close": Topmost().Close(); await Task.Delay(200); break;
            case "tree": await SelectTreeNode(arg); break;
            case "dump": Dump(); break;
            case "expect": Expect(arg); break;
            case "log": Log(arg); break;
            case "exit": _main.ForceClose(); break;
            case "quit": _main.Close(); break;
            default: throw new InvalidOperationException($"unknown command {cmd}");
        }
        await Task.Delay(120);
    }

    /// <summary>Selects a tree node by name (expanding parents) and scrolls it into view so it can be clicked.</summary>
    private async Task SelectTreeNode(string name)
    {
        var vm = _main.ViewModel;
        var node = vm.Root.Descendants().FirstOrDefault(n => n.Name == name) ?? throw new InvalidOperationException($"no tree node '{name}'");
        for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        vm.SelectedNode = node;
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        var tree = _main.FindControl<TreeView>("Tree")!;
        tree.TreeContainerFromItem(node)?.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        Log($"  tree '{name}' selected={vm.SelectedNode?.Name} container={(tree.TreeContainerFromItem(node) is null ? "none" : "ok")}");
    }

    // ---------------- screenshots ----------------

    private async Task Shot(string name)
    {
        await Task.Delay(250);
        var win = Topmost();
        var size = new PixelSize(Math.Max(1, (int)win.Bounds.Width), Math.Max(1, (int)win.Bounds.Height));
        var path = Path.Combine(_outDir, name + ".png");
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        try { rtb.Render(win); }
        catch { rtb.Render((Visual)win.GetVisualChildren().First()); }
        rtb.Save(path);
        Log($"  shot {name} ({win.Title}, {size.Width}x{size.Height})");
    }

    // ---------------- element lookup ----------------

    private static string Strip(string s) => s.Replace("_", "");

    private static string? TextOf(Control c) => c switch
    {
        MenuItem m => m.Header as string,
        TextBlock t => t.Text,
        ToggleButton tb => tb.Content as string,
        Button b => b.Content as string,
        TextBox tb => tb.Text,
        _ => null
    };

    /// <summary>Selector: #Name | @Text | #Scope@Text ; optional "~n" picks the n-th match (1-based).</summary>
    private static Control Find(string selector)
    {
        var index = 1;
        var tilde = selector.LastIndexOf('~');
        if (tilde > 0 && int.TryParse(selector[(tilde + 1)..], out var n)) { index = n; selector = selector[..tilde]; }

        IEnumerable<Control> candidates;
        Control? scopeControl;
        string? text = null, name = null;
        var at = selector.IndexOf('@');
        if (at >= 0) { text = selector[(at + 1)..]; selector = selector[..at]; }
        if (selector.StartsWith('#')) name = selector[1..];

        var roots = Windows().Reverse().Cast<Control>().ToList();
        if (name is not null)
        {
            scopeControl = roots.SelectMany(r => r.GetVisualDescendants().OfType<Control>().Prepend(r))
                .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
            if (scopeControl is null) throw new InvalidOperationException($"no visible control named {name}");
            if (text is null) return scopeControl;
            roots = new List<Control> { scopeControl };
        }
        candidates = roots.SelectMany(r => r.GetVisualDescendants().OfType<Control>())
            .Where(c => c.IsEffectivelyVisible && TextOf(c) is { } t && Strip(t) == text);
        // prefer the outermost control carrying the text (MenuItem/Button over its inner TextBlock)
        var list = candidates.Where(c => c is not TextBlock || !candidates.Any(o => o != c && o.GetVisualDescendants().Contains(c))).ToList();
        if (list.Count < index) throw new InvalidOperationException($"no visible control with text '{text}' (match {index} of {list.Count})");
        return list[index - 1];
    }

    private static (Window win, Point pt) CenterOf(Control c)
    {
        var win = TopLevel.GetTopLevel(c) as Window ?? throw new InvalidOperationException("control not in a window");
        var center = new Point(c.Bounds.Width / 2, c.Bounds.Height / 2);
        var pt = c.TranslatePoint(center, win) ?? throw new InvalidOperationException("cannot translate point");
        return (win, pt);
    }

    // ---------------- raw input ----------------

    private static ulong Ts => (ulong)Environment.TickCount64;

    private async Task Click(string target, MouseButton button, int clicks)
    {
        Window win; Point pt;
        if (target.Contains(',') && char.IsDigit(target[0]))
        {
            var parts = target.Split(',');
            win = Topmost();
            pt = new Point(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        else (win, pt) = CenterOf(Find(target));

        var root = (IInputRoot)win;
        var mods = button == MouseButton.Right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton;
        var down = button == MouseButton.Right ? RawPointerEventType.RightButtonDown : RawPointerEventType.LeftButtonDown;
        var up = button == MouseButton.Right ? RawPointerEventType.RightButtonUp : RawPointerEventType.LeftButtonUp;
        _raw.Pointer(win, RawPointerEventType.Move, pt, RawInputModifiers.None);
        for (var i = 0; i < clicks; i++)
        {
            _raw.Pointer(win, down, pt, mods);
            await Task.Delay(40);
            _raw.Pointer(win, up, pt, RawInputModifiers.None);
            await Task.Delay(60);
        }
        Log($"  {(button == MouseButton.Right ? "r" : "")}click{(clicks > 1 ? "x" + clicks : "")} {target} @ {pt.X:0},{pt.Y:0} in '{win.Title}'");
        await Task.Delay(200);
    }

    private async Task Move(string target)
    {
        Window win; Point pt;
        if (target.Contains(',') && char.IsDigit(target[0]))
        {
            var parts = target.Split(',');
            win = Topmost();
            pt = new Point(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        else (win, pt) = CenterOf(Find(target));
        _raw.Pointer(win, RawPointerEventType.Move, pt, RawInputModifiers.None);
        await Task.Delay(100);
    }

    private async Task Key(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var key = Enum.Parse<Key>(parts[0], true);
        var mods = RawInputModifiers.None;
        foreach (var m in parts.Skip(1))
            mods |= m.ToLowerInvariant() switch
            {
                "cmd" or "meta" => RawInputModifiers.Meta, "shift" => RawInputModifiers.Shift,
                "alt" or "option" => RawInputModifiers.Alt, "ctrl" or "control" => RawInputModifiers.Control, _ => RawInputModifiers.None
            };
        var physical = ToPhysical(key);
        var win = Topmost();
        var symbol = key is >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z && mods == RawInputModifiers.None ? key.ToString().ToLowerInvariant() : null;
        _raw.Key(win, RawKeyEventType.KeyDown, key, mods, physical, symbol);
        await Task.Delay(30);
        _raw.Key(win, RawKeyEventType.KeyUp, key, mods, physical, symbol);
        Log($"  key {arg} -> '{win.Title}' focus={FocusName()}");
        await Task.Delay(150);
    }

    private static PhysicalKey ToPhysical(Key key)
    {
        if (Enum.TryParse<PhysicalKey>(key.ToString(), out var pk)) return pk;
        return key switch
        {
            Avalonia.Input.Key.Back => PhysicalKey.Backspace, Avalonia.Input.Key.Return => PhysicalKey.Enter,
            Avalonia.Input.Key.Up => PhysicalKey.ArrowUp, Avalonia.Input.Key.Down => PhysicalKey.ArrowDown,
            Avalonia.Input.Key.Left => PhysicalKey.ArrowLeft, Avalonia.Input.Key.Right => PhysicalKey.ArrowRight,
            Avalonia.Input.Key.D0 => PhysicalKey.Digit0, Avalonia.Input.Key.D1 => PhysicalKey.Digit1, Avalonia.Input.Key.D2 => PhysicalKey.Digit2,
            Avalonia.Input.Key.D3 => PhysicalKey.Digit3, Avalonia.Input.Key.D4 => PhysicalKey.Digit4, Avalonia.Input.Key.D5 => PhysicalKey.Digit5,
            Avalonia.Input.Key.LWin => PhysicalKey.MetaLeft, Avalonia.Input.Key.LeftCtrl => PhysicalKey.ControlLeft,
            Avalonia.Input.Key.LeftShift => PhysicalKey.ShiftLeft, Avalonia.Input.Key.LeftAlt => PhysicalKey.AltLeft,
            _ => PhysicalKey.None
        };
    }

    private async Task TypeText(string text)
    {
        var win = Topmost();
        foreach (var ch in text)
        {
            _raw.Text(win, ch.ToString());
            await Task.Delay(15);
        }
        Log($"  type '{text}' -> focus={FocusName()}");
    }

    /// <summary>Clicks the editor cell of the property-grid row with the given display name.</summary>
    private async Task Cell(string displayName)
    {
        var label = Windows().Reverse().SelectMany(w => w.GetVisualDescendants().OfType<TextBlock>())
            .FirstOrDefault(t => t.Text == displayName && t.IsEffectivelyVisible && t.FindAncestorOfType<Controls.PropertyGrid>() is not null)
            ?? throw new InvalidOperationException($"no grid row '{displayName}'");
        var row = label.FindAncestorOfType<Grid>() ?? throw new InvalidOperationException("row grid not found");
        var editor = row.Children.OfType<Border>().Last().Child ?? throw new InvalidOperationException("editor not found");
        var (win, pt) = CenterOf(editor);
        _raw.Pointer(win, RawPointerEventType.Move, pt, RawInputModifiers.None);
        _raw.Pointer(win, RawPointerEventType.LeftButtonDown, pt, RawInputModifiers.LeftMouseButton);
        await Task.Delay(40);
        _raw.Pointer(win, RawPointerEventType.LeftButtonUp, pt, RawInputModifiers.None);
        await Task.Delay(150);
        if (editor is TextBox tb) { tb.Focus(); tb.SelectAll(); }
        Log($"  cell '{displayName}' -> {editor.GetType().Name} focus={FocusName()}");
    }

    private static string FocusName()
    {
        var f = TopLevel.GetTopLevel(Topmost())?.FocusManager?.GetFocusedElement() as Control;
        return f is null ? "(none)" : $"{f.GetType().Name}{(f.Name is null ? "" : "#" + f.Name)}";
    }

    // ---------------- state ----------------

    private void Dump()
    {
        var vm = _main.ViewModel;
        var sb = new StringBuilder();
        sb.AppendLine($"  windows: {string.Join(" | ", Windows().Select(w => $"'{w.Title}' {w.Bounds.Width:0}x{w.Bounds.Height:0}"))}");
        sb.AppendLine($"  selected: {vm.SelectedNode?.Name} ({vm.SelectedNode?.GetType().Name}) host={vm.SelectedNode?.Hostname} parent={vm.SelectedNode?.Parent?.Name}");
        sb.AppendLine($"  tabs: {vm.Tabs.Count} current={_main.CurrentTabForTest?.Title} state={_main.CurrentTabForTest?.State} desktop={_main.CurrentTabForTest?.Session?.Width}x{_main.CurrentTabForTest?.Session?.Height}");
        var focusedText = TopLevel.GetTopLevel(Topmost())?.FocusManager?.GetFocusedElement() is TextBox ftb ? $" text='{ftb.Text}'" : "";
        sb.AppendLine($"  dirty={vm.IsDirty} file={vm.FilePath} theme={Application.Current!.ActualThemeVariant} focus={FocusName()}{focusedText}");
        sb.AppendLine($"  tree: {string.Join(", ", vm.Root.Children.Select(c => c.Name + (c is ContainerInfo ci ? $"[{ci.Children.Count}]" : "")))}");
        sb.Append($"  notifications: {string.Join(" || ", Notifications.Instance.Messages.TakeLast(3).Select(m => m.Text))}");
        Log(sb.ToString());
    }

    private void Expect(string arg)
    {
        var sp = arg.IndexOf(' ');
        var what = sp < 0 ? arg : arg[..sp];
        var expected = sp < 0 ? "" : arg[(sp + 1)..].Trim();
        var vm = _main.ViewModel;
        var tab = _main.CurrentTabForTest;
        string actual = what switch
        {
            "selected" => vm.SelectedNode?.Name ?? "(null)",
            "hostname" => vm.SelectedNode?.Hostname ?? "(null)",
            "tabs" => vm.Tabs.Count.ToString(),
            "windows" => Windows().Count().ToString(),
            "window" => Topmost().Title ?? "",
            "tabstate" => tab?.State.ToString() ?? "(none)",
            "desktop" => tab?.Session is { } s ? $"{s.Width}x{s.Height}" : "(none)",
            "theme" => Application.Current!.ActualThemeVariant.ToString(),
            "dirty" => vm.IsDirty.ToString(),
            "focus" => FocusName(),
            "children" => (vm.SelectedNode as ContainerInfo)?.Children.Count.ToString() ?? "(not container)",
            "parent" => vm.SelectedNode?.Parent?.Name ?? "(null)",
            "fullscreen" => (_main.WindowState == WindowState.FullScreen).ToString(),
            _ => "?"
        };
        bool ok = what switch
        {
            "notif" => Notifications.Instance.Messages.Any(m => m.Text.Contains(expected, StringComparison.OrdinalIgnoreCase)),
            "file" => vm.Store is not null && (vm.Store is Config.XmlConnectionStore
                ? File.Exists(vm.FilePath) && File.ReadAllText(vm.FilePath!).Contains(expected)
                : Config.ConnectionsFile.SerializeToText(vm.Store.Load()).Contains(expected)),
            "visible" => Find(expected.Split(' ')[0]).IsEffectivelyVisible == bool.Parse(expected.Split(' ')[1]),
            "text" => TextOf(Find(expected.Split(' ')[0])) == expected[(expected.IndexOf(' ') + 1)..],
            "exists" => TryFind(expected),
            "missing" => !TryFind(expected),
            _ => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        };
        if (what == "?") ok = false;
        if (what is "notif" or "file" or "visible" or "text" or "exists" or "missing") actual = ok ? "ok" : "no";
        if (ok) { _pass++; Log($"  PASS {what} = {expected}"); }
        else { _fail++; Log($"  FAIL {what}: expected '{expected}', actual '{actual}'"); }
    }

    private static bool TryFind(string selector)
    {
        try { Find(selector); return true; } catch { return false; }
    }
}

/// <summary>Reflection shim over Avalonia's [PrivateApi] raw input types (hidden from the reference assemblies).</summary>
internal sealed class RawInput
{
    private readonly object _mouse;
    private readonly object _keyboard;
    private readonly Type _pointerArgs = typeof(RawPointerEventArgs);
    private readonly Type _keyArgs = typeof(RawKeyEventArgs);
    private readonly Type _textArgs = typeof(RawTextInputEventArgs);

    public RawInput()
    {
        var asm = typeof(RawPointerEventArgs).Assembly;
        var pointerType = asm.GetType("Avalonia.Input.Pointer")!;
        var nextId = (int)pointerType.GetMethod("GetNextFreeId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
        var pointer = Activator.CreateInstance(pointerType, nextId, PointerType.Mouse, true)!;
        _mouse = Activator.CreateInstance(typeof(MouseDevice), pointer)!;
        // the platform-registered keyboard device tracks focus; a fresh one would deliver text to nobody
        var locator = asm.GetType("Avalonia.AvaloniaLocator")!;
        var current = locator.GetProperty("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var getService = current.GetType().GetMethod("GetService", new[] { typeof(Type) })!;
        _keyboard = getService.Invoke(current, new object[] { typeof(IKeyboardDevice) }) ?? Activator.CreateInstance<KeyboardDevice>();
        _mouse = getService.Invoke(current, new object[] { typeof(IMouseDevice) }) ?? _mouse;
    }

    private static ulong Ts => (ulong)Environment.TickCount64;

    private static void Dispatch(Window win, object args)
    {
        var impl = typeof(TopLevel).GetProperty("PlatformImpl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(win);
        if (impl is null) return; // window closed meanwhile (e.g. Enter closed a dialog before KeyUp)
        var input = (Delegate?)impl.GetType().GetProperty("Input")!.GetValue(impl) ?? throw new InvalidOperationException("no input handler");
        input.DynamicInvoke(args);
    }

    public void Pointer(Window win, RawPointerEventType type, Point pt, RawInputModifiers mods)
    {
        var args = Activator.CreateInstance(_pointerArgs, _mouse, Ts, (IInputRoot)win, type, pt, mods)!;
        Dispatch(win, args);
    }

    public void Key(Window win, RawKeyEventType type, Key key, RawInputModifiers mods, PhysicalKey physical, string? symbol)
    {
        var ctor = _keyArgs.GetConstructors().First(c => c.GetParameters().Length == 8);
        var args = ctor.Invoke(new object?[] { _keyboard, Ts, (IInputRoot)win, type, key, mods, physical, symbol });
        Dispatch(win, args);
    }

    public void Text(Window win, string text)
    {
        var args = Activator.CreateInstance(_textArgs, _keyboard, Ts, (IInputRoot)win, text)!;
        Dispatch(win, args);
    }
}

#endif
