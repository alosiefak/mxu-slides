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
}
