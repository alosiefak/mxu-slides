namespace MxU.Slides.Windows.ViewModels;

/// <summary>The stage layout editor's hook into the live stage (Views/StageLayoutEditorWindow.xaml).</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// A stage layout was changed in the editor (the library's own copy, edited in place): every stage
    /// screen and preview showing it is drawn again now, before the change is saved.
    /// </summary>
    public void StageLayoutEdited() => RenderStage(Rendering.SceneTransition.Cut, refreshOnly: true);
}
