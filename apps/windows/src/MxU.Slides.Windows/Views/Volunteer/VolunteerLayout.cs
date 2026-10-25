using System.Windows;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Volunteer;

/// <summary>
/// Swaps the main window's content for the volunteer layout while volunteer mode is on, and back
/// when it ends. The usual layout isn't thrown away: it waits, exactly as it was, until the
/// volunteer leaves. The live preview moves to the volunteer layout's, and windows that edit
/// (the combo, scheduler or timeline editors, Settings) close; preview pop-outs stay. Volunteer
/// mode survives a restart, so this also puts the layout up when the window first opens.
/// </summary>
public static class VolunteerLayout
{
    public static void Attach(MainWindow window, MainViewModel model)
    {
        object? usual = null;
        VolunteerView? shown = null;
        var title = window.Title;

        void Apply()
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(Apply);
                return;
            }
            if (model.IsVolunteer && shown is null)
            {
                foreach (var owned in window.OwnedWindows.OfType<Window>().Where(w => w is not PreviewPopOutWindow).ToList()) owned.Close();
                usual = window.Content;
                shown = new VolunteerView(model);
                window.Content = shown;
                window.Title = $"{title} — Volunteer Mode";
                model.AudiencePreview = shown.Preview;
                model.RenderOutputs(SceneTransition.Cut, refreshOnly: true);
                window.Focus();
            }
            else if (!model.IsVolunteer && shown is not null)
            {
                window.Content = usual;
                shown = null;
                window.Title = title;
                model.AudiencePreview = window.AudiencePreview;
                model.RenderOutputs(SceneTransition.Cut, refreshOnly: true);
                window.Focus();
            }
        }

        model.VolunteerChanged += Apply;
        window.Loaded += (_, _) => Apply();
    }

    /// <summary>The volunteer layout on show now, if any (the self-test pictures it).</summary>
    internal static VolunteerView? Current(MainWindow window) => window.Content as VolunteerView;
}
