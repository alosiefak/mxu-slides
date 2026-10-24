using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The service's menus, built when they open so they show what's true now: the service menu
/// (New, Rename, Change Date, Duplicate, Versions, Trash, as in the Mac's service picker) and the
/// extra options on a row's right-click menu (Hide, Output Preset, Use Main's).
/// </summary>
public static class ServiceMenus
{
    private static MenuItem Item(string header, Action action, bool enabled = true, bool? check = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsCheckable = check is not null, IsChecked = check == true };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The ⋯ menu beside the service picker.</summary>
    public static void ShowServiceMenu(Window owner, MainViewModel model, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        var service = model.SelectedService;
        var has = service is not null;
        menu.Items.Add(Item("New service", () => model.NewServiceCommand.Execute(null)));
        menu.Items.Add(Item("Rename service…", () =>
        {
            if (service is not null && InputDialog.Ask(owner, "Rename service", "Name", service.Name) is { } name) model.RenameService(service, name);
        }, has));
        menu.Items.Add(Item("Change date…", () =>
        {
            if (ServiceDateWindow.Ask(owner, service?.Name ?? "Service", model.SelectedServiceDate) is { } date) model.ChangeServiceDate(date);
        }, has));
        menu.Items.Add(Item("Duplicate", () => model.DuplicateServiceCommand.Execute(null), has));
        var versions = new MenuItem { Header = "Versions", IsEnabled = has };
        foreach (var entry in VersionItems(owner, model)) versions.Items.Add(entry);
        menu.Items.Add(versions);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move service to Trash", () =>
        {
            if (service is not null && MessageBox.Show(owner, $"Move \"{service.Name}\" to the library's Trash folder?", "MxU Slides",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model.DeleteServiceCommand.Execute(null);
        }, has));
        menu.IsOpen = true;
    }

    /// <summary>The version chip's menu (the same items as Service › Versions).</summary>
    public static void ShowVersionMenu(Window owner, MainViewModel model, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var entry in VersionItems(owner, model)) menu.Items.Add(entry);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Which version this computer runs (Main, or one of them, each with the computers running it),
    /// then New, Rename and Delete for the running one. As the Mac's serviceVersionMenu.
    /// </summary>
    private static IEnumerable<object> VersionItems(Window owner, MainViewModel model)
    {
        if (model.SelectedService is not { } service) yield break;
        var running = model.SelectedServiceRunningVersion;
        yield return Item("Main", () => model.ChooseServiceVersion(service.Id, null), check: running is null);
        foreach (var version in model.SelectedServiceVersions)
        {
            var computers = string.Join(", ", (version.Stations ?? []).Select(s => s.Name));
            yield return Item(computers.Length == 0 ? version.Name : $"{version.Name} — {computers}",
                () => model.ChooseServiceVersion(service.Id, version.Id), check: running?.Id == version.Id);
        }
        yield return new Separator();
        yield return Item("New version…", () =>
        {
            if (InputDialog.Ask(owner, "New version",
                    "A version starts as a copy of Main. This computer runs it; changes you make while it runs stay in the version, and Main is untouched.",
                    "") is { } name)
                model.CreateServiceVersion(service.Id, name);
        });
        if (running is null) yield break;
        yield return Item($"Rename “{running.Name}”…", () =>
        {
            if (InputDialog.Ask(owner, "Rename version", "Name", running.Name) is { } name) model.RenameServiceVersion(service.Id, running.Id, name);
        });
        yield return Item($"Delete “{running.Name}”…", () =>
        {
            if (MessageBox.Show(owner, "Its changes are deleted, and any computer running it goes back to Main.", "Delete version?",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                model.DeleteServiceVersion(service.Id, running.Id);
        });
    }

    private const string ExtraTag = "show-flow";

    /// <summary>Adds Hide, Output Preset and Use Main's to a row's right-click menu as it opens.</summary>
    public static void AddItemOptions(ContextMenu menu, MainViewModel model)
    {
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, ExtraTag)).ToList()) menu.Items.Remove(old);
        if (model.SelectedServiceItem is not { } row) return;
        var item = row.Item;
        void Add(FrameworkElement element)
        {
            element.Tag = ExtraTag;
            menu.Items.Add(element);
        }
        Add(new Separator());
        if (item.ItemKind is ServiceItemKind.Presentation or ServiceItemKind.Media && model.Library.All<OutputPreset>() is { Count: > 0 } presets)
        {
            var preset = new MenuItem { Header = "Output preset" };
            preset.Items.Add(Item("Keep Current Look", () => model.SetServiceItemOutputPreset(item.Id, null), check: string.IsNullOrEmpty(item.OutputPresetId)));
            preset.Items.Add(new Separator());
            foreach (var choice in presets)
                preset.Items.Add(Item(choice.Name, () => model.SetServiceItemOutputPreset(item.Id, choice.Id), check: item.OutputPresetId == choice.Id));
            Add(preset);
        }
        Add(Item("Hide", () => model.SetServiceItemHidden(item.Id, true)));
        if (row.IsChanged) Add(Item("Use Main's", () => model.UseMainForServiceItem(item.Id)));
    }
}
