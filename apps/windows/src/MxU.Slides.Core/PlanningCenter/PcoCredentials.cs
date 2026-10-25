using System.Net.Http.Headers;
using System.Text;

namespace MxU.Slides.Core.PlanningCenter;

/// <summary>
/// A Planning Center Personal Access Token: the application id and secret a church makes at
/// api.planningcenteronline.com. They sign every request with HTTP Basic, so they're as good as
/// a password: the app keeps them encrypted for this Windows user (DPAPI, in the Windows app),
/// sends them only in the Authorization header to Planning Center itself, and never writes them
/// to a log, a URL or the library.
/// <para>
/// <see cref="ToString"/> is overridden so that printing a credentials object by mistake (in a log
/// line, an exception or the debugger) shows only that it exists, never the values.
/// </para>
/// Windows only: the Mac has no Planning Center import of its own.
/// </summary>
public sealed record PcoCredentials(string ApplicationId, string Secret)
{
    public bool IsComplete => ApplicationId.Trim().Length > 0 && Secret.Trim().Length > 0;

    /// <summary>"Basic base64(id:secret)", for the Authorization header and nowhere else.</summary>
    public AuthenticationHeaderValue BasicHeader() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApplicationId.Trim()}:{Secret.Trim()}")));

    // A record prints its values by default; another record holding this one prints it through this too.
    public override string ToString() => "PcoCredentials { (hidden) }";
}
