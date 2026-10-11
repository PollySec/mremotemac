using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MRemoteMac.Converters;
using MRemoteMac.Models;

namespace MRemoteMac.Controls;

public abstract class GridRow : ObservableObject { }

public sealed partial class CategoryRow : GridRow
{
    public string Name { get; }
    [ObservableProperty] private bool _isExpanded = true;
    public ObservableCollection<PropertyRow> Properties { get; } = new();
    public CategoryRow(string name) => Name = name;
}

/// <summary>One editable line in the grid; writes straight through to the model property.</summary>
public sealed partial class PropertyRow : GridRow
{
    public ConnectionInfo Target { get; }
    public PropertyInfo Property { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public bool IsPassword { get; }
    public bool IsInheritanceRow { get; }
    public CategoryRow Category { get; }
    public event Action<PropertyRow>? Changed;

    public PropertyRow(ConnectionInfo target, PropertyInfo prop, CategoryRow category, bool inheritanceRow)
    {
        Target = target;
        Property = prop;
        Category = category;
        IsInheritanceRow = inheritanceRow;
        DisplayName = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? prop.Name;
        Description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        IsPassword = prop.GetCustomAttribute<PasswordPropertyTextAttribute>()?.Password == true;
    }

    public bool IsInherited => !IsInheritanceRow && Target.Inherits(Property.Name);
    public bool IsReadOnly => IsInherited;
    public FontStyle FontStyle => IsInherited ? FontStyle.Italic : FontStyle.Normal;

    public Type EditorType => IsInheritanceRow ? typeof(bool) : Property.PropertyType;
    public bool IsEnum => EditorType.IsEnum;
    public bool IsBool => EditorType == typeof(bool);
    public bool IsInt => EditorType == typeof(int);
    public bool IsIcon => !IsInheritanceRow && Property.Name == nameof(ConnectionInfo.Icon);
    public bool IsText => !IsEnum && !IsBool && !IsInt && !IsIcon;

    public IEnumerable<object> EnumValues => IsEnum ? Enum.GetValues(EditorType).Cast<object>() : Array.Empty<object>();
    public static IEnumerable<string> BoolValues { get; } = new[] { "True", "False" };
    public static IEnumerable<string> IconValues => ImageCache.AvailableIcons();

    public object? Value
    {
        get
        {
            if (IsInheritanceRow) return Target.Inheritance.Contains(Property.Name);
            return Target.GetEffective(Property.Name);
        }
        set
        {
            if (IsInheritanceRow)
            {
                var on = value is true || value is string s && s == "True";
                if (on) Target.Inheritance.Add(Property.Name); else Target.Inheritance.Remove(Property.Name);
            }
            else
            {
                if (IsInherited) return;
                Property.SetValue(Target, value);
            }
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(TextValue));
            OnPropertyChanged(nameof(BoolText));
            Changed?.Invoke(this);
        }
    }

    public string? TextValue
    {
        get => Value is null ? "" : Convert.ToString(Value, CultureInfo.InvariantCulture);
        set
        {
            if (IsInt) { if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) Value = i; }
            else Value = value ?? "";
        }
    }

    public string BoolText
    {
        get => Value is true ? "True" : "False";
        set => Value = value == "True";
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(BoolText));
        OnPropertyChanged(nameof(IsInherited));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(FontStyle));
    }
}

/// <summary>A WinForms PropertyGrid look-alike: categorized rows, name column + editor column, help pane.</summary>
public sealed class PropertyGrid : UserControl
{
    public static readonly string[] CategoryOrder = { "Display", "Connection", "Protocol", "RD Gateway", "Appearance", "Redirect", "Miscellaneous" };

    private readonly ItemsControl _items;
    private readonly TextBlock _helpTitle;
    private readonly TextBlock _helpText;
    private ConnectionInfo? _target;
    private bool _inheritanceMode;
    private readonly List<PropertyRow> _rows = new();

    public event Action<ConnectionInfo, string>? ValueChanged;

    public static readonly StyledProperty<double> NameColumnWidthProperty =
        AvaloniaProperty.Register<PropertyGrid, double>(nameof(NameColumnWidth), 130);

    public double NameColumnWidth { get => GetValue(NameColumnWidthProperty); set => SetValue(NameColumnWidthProperty, value); }

    public PropertyGrid()
    {
        _items = new ItemsControl { ItemTemplate = BuildTemplate() };
        _helpTitle = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(6, 4, 6, 0) };
        _helpText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 4) };
        var help = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("MrBorderBrush").ToBinding(),
            [!Border.BackgroundProperty] = this.GetResourceObservable("MrDialogBackgroundBrush").ToBinding(),
            MinHeight = 52,
            Child = new StackPanel { Children = { _helpTitle, _helpText } }
        };
        var scroll = new ScrollViewer { Content = _items, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dock = new DockPanel();
        DockPanel.SetDock(help, Dock.Bottom);
        dock.Children.Add(help);
        dock.Children.Add(scroll);
        Content = new Border
        {
            [!Border.BackgroundProperty] = this.GetResourceObservable("MrTextBoxBackgroundBrush").ToBinding(),
            Child = dock
        };
    }

    public bool InheritanceMode
    {
        get => _inheritanceMode;
        set { _inheritanceMode = value; Rebuild(); }
    }

    public ConnectionInfo? Target
    {
        get => _target;
        set { _target = value; Rebuild(); }
    }

    public void RefreshValues()
    {
        foreach (var r in _rows) r.Refresh();
    }

    private void Rebuild()
    {
        _rows.Clear();
        _helpTitle.Text = "";
        _helpText.Text = "";
        if (_target is null) { _items.ItemsSource = null; return; }

        var categories = new Dictionary<string, CategoryRow>();
        var list = new List<GridRow>();
        var props = ConnectionInfo.GridProperties
            .Where(p => !_inheritanceMode || ConnectionInfo.InheritableProperties.Contains(p.Name))
            .Where(p => !(_target is RootNodeInfo) || p.Name is nameof(ConnectionInfo.Name))
            .Where(p => !_target.IsContainer || p.Name is not (nameof(ConnectionInfo.Hostname) or nameof(ConnectionInfo.Favorite)))
            .ToList();

        // ordering by category, then declaration order
        foreach (var catName in CategoryOrder)
        {
            var catProps = props.Where(p => (p.GetCustomAttribute<CategoryAttribute>()?.Category ?? "Misc") == catName).ToList();
            if (catProps.Count == 0) continue;
            var cat = new CategoryRow(catName);
            categories[catName] = cat;
            foreach (var p in catProps)
            {
                var row = new PropertyRow(_target, p, cat, _inheritanceMode);
                row.Changed += r =>
                {
                    ValueChanged?.Invoke(r.Target, r.Property.Name);
                    if (r.IsInheritanceRow) RefreshValues();
                };
                cat.Properties.Add(row);
                _rows.Add(row);
            }
        }
        foreach (var cat in categories.Values)
        {
            list.Add(cat);
            foreach (var r in cat.Properties) list.Add(r);
        }
        _items.ItemsSource = list;
    }

    private FuncDataTemplate<GridRow> BuildTemplate()
    {
        return new FuncDataTemplate<GridRow>((row, _) => row switch
        {
            CategoryRow c => BuildCategory(c),
            PropertyRow p => BuildProperty(p),
            _ => new TextBlock()
        }, supportsRecycling: false);
    }

    private Border BuildCategory(CategoryRow c)
    {
        var arrow = new TextBlock { Text = c.IsExpanded ? "⊟" : "⊞", Width = 14, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { arrow, new TextBlock { Text = c.Name, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center } } };
        var border = new Border
        {
            Height = 20,
            Padding = new Thickness(4, 0),
            Child = panel,
            [!Border.BackgroundProperty] = this.GetResourceObservable("MrHeaderBackgroundBrush").ToBinding()
        };
        border.PointerPressed += (_, _) =>
        {
            c.IsExpanded = !c.IsExpanded;
            arrow.Text = c.IsExpanded ? "⊟" : "⊞";
            foreach (var r in c.Properties)
            {
                var container = _items.ContainerFromItem(r);
                if (container is not null) container.IsVisible = c.IsExpanded;
            }
        };
        return border;
    }

    private Grid BuildProperty(PropertyRow p)
    {
        var grid = new Grid { Height = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(NameColumnWidth)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var name = new Border
        {
            Padding = new Thickness(16, 0, 4, 0),
            BorderThickness = new Thickness(0, 0, 1, 1),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("MrGridLineBrush").ToBinding(),
            [!Border.BackgroundProperty] = this.GetResourceObservable("MrTextBoxBackgroundBrush").ToBinding(),
            Child = new TextBlock { Text = p.DisplayName, VerticalAlignment = VerticalAlignment.Center, FontStyle = p.FontStyle, TextTrimming = TextTrimming.CharacterEllipsis }
        };
        Grid.SetColumn(name, 0);
        var editorBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("MrGridLineBrush").ToBinding(),
            Child = BuildEditor(p)
        };
        Grid.SetColumn(editorBorder, 1);
        grid.Children.Add(name);
        grid.Children.Add(editorBorder);
        grid.PointerPressed += (_, _) => { _helpTitle.Text = p.DisplayName; _helpText.Text = p.Description; };
        grid.GotFocus += (_, _) => { _helpTitle.Text = p.DisplayName; _helpText.Text = p.Description; };
        return grid;
    }

    private static Control BuildEditor(PropertyRow p)
    {
        if (p.IsBool)
        {
            var cb = new ComboBox { ItemsSource = PropertyRow.BoolValues, SelectedItem = p.BoolText, IsEnabled = !p.IsReadOnly };
            cb.Classes.Add("grid");
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != p.BoolText) p.BoolText = s; };
            return cb;
        }
        if (p.IsEnum)
        {
            var cb = new ComboBox { ItemsSource = p.EnumValues.ToList(), SelectedItem = p.Value, IsEnabled = !p.IsReadOnly };
            cb.Classes.Add("grid");
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is not null && !Equals(cb.SelectedItem, p.Value)) p.Value = cb.SelectedItem; };
            return cb;
        }
        if (p.IsIcon)
        {
            var icons = PropertyRow.IconValues.ToList();
            var cb = new ComboBox { ItemsSource = icons, SelectedItem = p.Value as string, IsEnabled = !p.IsReadOnly };
            cb.Classes.Add("grid");
            cb.ItemTemplate = new FuncDataTemplate<string>((s, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Image { Source = ImageCache.ConnectionIcon(s), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) },
                    new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center }
                }
            });
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != (string?)p.Value) p.Value = s; };
            return cb;
        }
        var tb = new TextBox { Text = p.TextValue, IsReadOnly = p.IsReadOnly, FontStyle = p.FontStyle };
        tb.Classes.Add("grid");
        if (p.IsPassword) tb.PasswordChar = '●';
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
        void Commit()
        {
            if (tb.Text != p.TextValue) p.TextValue = tb.Text;
        }
        return tb;
    }
}
