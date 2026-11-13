namespace MxU.Slides.Windows.Infrastructure;

// The service assistant's choices on this computer (Phase 12). The API key is NOT here: it's kept
// encrypted in its own file (MxU.Slides.Assistant/AssistantKeyVault.cs), never in settings.json.
public sealed partial class AppSettings
{
    /// <summary>
    /// The PC's on/off switch for everything that talks to Anthropic. Off until a key is saved;
    /// with it off nothing is ever sent, whatever else is set.
    /// </summary>
    public bool AssistantEnabled { get; set; }

    /// <summary>The Claude model to use ("claude-opus-5-5" or "claude-haiku-5-5"); null = the default.</summary>
    public string? AssistantModel { get; set; }

    /// <summary>The sound input the scripture spotter listens to (the device's id); null = the first one found.</summary>
    public string? SpotterInputUid { get; set; }

    /// <summary>Where live captions show: "stage" (the stage screens), "audience" or "both". Null = stage.</summary>
    public string? CaptionsTarget { get; set; }

    /// <summary>The language captions are translated into by the assistant ("Spanish"); null = shown as heard.</summary>
    public string? CaptionsLanguage { get; set; }

    /// <summary>The language last chosen for translating a deck.</summary>
    public string? TranslateLanguage { get; set; }
}