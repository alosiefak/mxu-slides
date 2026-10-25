using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.PlanningCenter;

/// <summary>
/// One row as Planning Center last gave it: what the import set on the service row. Comparing
/// the row now with this tells the operator's changes apart from Planning Center's.
/// </summary>
public sealed record PlanLinkedItem(
    string PcoItemId,
    string ServiceItemId,
    string Name,
    ServiceItemKind ItemKind,
    string RefId,
    string? ArrangementId,
    int? Duration,
    string? PcoSongId);

/// <summary>
/// Which Planning Center plan a service came from, and what the last import of it produced.
/// <para>
/// The shared document format has places for a row's Planning Center ids (ServiceItem's
/// <c>mxuPcoItemId</c>, <c>mxuPcoSongId</c>, …), and the import fills them in. It has no place for
/// the plan's own id: the Service's <c>mxuPlanHexId</c> belongs to MxU's own planning server, and
/// writing a Planning Center id there could confuse a Mac. So the plan id, and the snapshot a
/// re-import needs, are kept beside the library as a Windows extra
/// (<c>Windows extras/planning-center/&lt;service id&gt;.json</c>), where the Mac never looks.
/// </para>
/// </summary>
public sealed record PlanLink(
    string ServiceTypeId,
    string ServiceTypeName,
    string PlanId,
    string ImportedName,
    string ImportedDate,
    DateTimeOffset ImportedAt,
    IReadOnlyList<PlanLinkedItem> Items)
{
    /// <summary>The kind of Windows extra links are kept under.</summary>
    public const string ExtraKind = "planning-center";

    public static PlanLink? For(LibraryStore library, string serviceId) => library.Extras.Get<PlanLink>(ExtraKind, serviceId);

    public static void Save(LibraryStore library, string serviceId, PlanLink link) => library.Extras.Save(ExtraKind, serviceId, link);

    /// <summary>The service a plan was imported into, if it's still in the library, with its link.</summary>
    public static (Service Service, PlanLink Link)? Find(LibraryStore library, string planId)
    {
        foreach (var serviceId in library.Extras.Owners(ExtraKind))
        {
            if (For(library, serviceId) is not { } link || link.PlanId != planId) continue;
            if (library.Get<Service>(serviceId) is { } service) return (service, link);
        }
        return null;
    }

    /// <summary>The plan ids the library has services for (the import window marks them "Imported").</summary>
    public static Dictionary<string, string> ImportedPlans(LibraryStore library)
    {
        var plans = new Dictionary<string, string>();
        foreach (var serviceId in library.Extras.Owners(ExtraKind))
            if (For(library, serviceId) is { } link && library.Get<Service>(serviceId) is not null) plans[link.PlanId] = serviceId;
        return plans;
    }
}
