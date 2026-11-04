using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The stage layout editor window: design what the stage (confidence) monitors show. One is open
/// at a time; <see cref="Open"/> brings it forward on the layout you ask for. The logic is in
/// <see cref="StageLayoutEditorViewModel"/>; this file asks the questions (names, "are you sure?"),
/// builds the New and Add menus, and draws the safe margin on the canvas.
/// </summary>
public partial class StageLayoutEditorWindow : Window
{
    private static StageLayoutEditorWindow? open;
    private readonly DispatcherTimer liveTimer;

    private static readonly Pen SafePen = MakeSafePen();

    private StageLayoutEditorWindow(MainViewModel main, string? layoutId)
    {
        InitializeComponent();
        Model = new StageLayoutEditorViewModel(main, layoutId);
        DataContext = Model;
        Model.Canvas = CanvasEditor;
        CanvasEditor.Decorate = DrawSafeArea;
        // The safe margin's guide moves when you pick another margin (or another layout's shape).
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StageLayoutEditorViewModel.SafeArea) or "" or null) CanvasEditor.Refresh();
        };
        // Showing what's live: redraw each second, so the clock and timers in the boxes move.
        liveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Model.LiveTick(), Dispatcher);
        liveTimer.Start();
        Closed += (_, _) =>
        {
            liveTimer.Stop();
            CanvasEditor.Decorate = null;
            Model.Dispose();
            if (ReferenceEquals(open, this)) open = null;
        };
    }

    public StageLayoutEditorViewModel Model { get; }

    /// <summary>
    /// Opens the stage layout editor (or brings the open one forward) on <paramref name="layoutId"/>,
    /// or on the first layout when that's null. It doesn't block.
    /// </summary>
    public static StageLayoutEditorWindow Open(Window owner, MainViewModel main, string? layoutId)
    {
        if (open is { } existing)
        {
            if (layoutId is not null) existing.Model.Open(layoutId);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new StageLayoutEditorWindow(main, layoutId) { Owner = owner };
        // Fit a small laptop screen: never bigger than the space left by the taskbar.
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width - 20);
        window.MinHeight = Math.Min(window.MinHeight, area.Height - 20);
        window.Width = Math.Min(window.Width, area.Width - 20);
        window.Height = Math.Min(window.Height, area.Height - 20);
        open = window;
        window.Show();
        return window;
    }

    /// <summary>The safe margin: a dashed line inside the canvas edges, drawn under the selection.</summary>
    private void DrawSafeArea(DrawingContext dc, Func<SceneRect, Rect> toView)
    {
        if (Model.SafeArea is not { } safe) return;
        dc.DrawRectangle(null, SafePen, toView(safe));
    }

    private static Pen MakeSafePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x4F, 0xC3, 0xF7)), 1) { DashStyle = new DashStyle([6, 4], 0) };
        pen.Freeze();
        return pen;
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Enter in a one-line box applies it straight away.</summary>
    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // MARK: Layouts

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var template in Model.Templates)
        {
            var item = new MenuItem { Header = template == ConfidenceLayoutTemplate.Blank ? "Blank" : template.Title() };
            item.Click += (_, _) => Model.NewLayout(template);
            menu.Items.Add(item);
        }
        Show(menu, NewButton);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Current is not { } layout || !Model.CanEdit) return;
        if (InputDialog.Ask(this, "Rename layout", "A new name for the stage layout:", layout.Name) is { } name && !Model.RenameLayout(name) && name.Trim().Length == 0)
            Model.Notice = "A layout needs a name.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Current is not { } layout) return;
        var screens = Model.ScreensUsing;
        var message = $"Delete the stage layout \"{layout.Name}\"? It goes to the library's Trash folder."
            + (screens > 0 ? $"\n\n{screens} stage screen{(screens == 1 ? " shows" : "s show")} it; {(screens == 1 ? "it goes" : "they go")} back to the built-in layout." : "");
        if (MessageBox.Show(this, message, "Stage Layouts", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Model.DeleteLayout();
    }

    // MARK: Boxes

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var choice in Model.BoxChoices)
        {
            var item = new MenuItem { Header = choice.Title, ToolTip = choice.Description };
            // A timer box asks which timer; the rest are added at once.
            var timers = choice.Kind switch
            {
                StageBoxKind.Timer => Model.PanelTimers,
                StageBoxKind.ServiceTimer => Model.ServiceTimers,
                _ => null,
            };
            if (timers is null)
            {
                item.Click += (_, _) => Model.AddBox(choice.Kind);
            }
            else
            {
                foreach (var timer in timers)
                {
                    var sub = new MenuItem { Header = timer.Label };
                    sub.Click += (_, _) => Model.AddBox(choice.Kind, timer.Value);
                    item.Items.Add(sub);
                }
                if (item.Items.Count == 0) item.IsEnabled = false;
            }
            menu.Items.Add(item);
        }
        Show(menu, AddButton);
    }

    private static void Show(ContextMenu menu, FrameworkElement under)
    {
        menu.PlacementTarget = under;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
