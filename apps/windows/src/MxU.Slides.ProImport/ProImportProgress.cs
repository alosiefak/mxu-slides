namespace MxU.Slides.ProImport;

/// <summary>A progress step: the phase ("Presentations", "Themes", "Media", …), the item being worked on, and counts when known.</summary>
public readonly record struct ProImportProgress(string Phase, string Detail = "", int Completed = 0, int? Total = null);
