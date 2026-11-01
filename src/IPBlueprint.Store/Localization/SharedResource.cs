namespace IPBlueprint.Store.Localization;

/// <summary>
/// Marker for the site's text. Keys are the English text itself, so English needs no resource file and a missing
/// Swedish entry shows up as English rather than as a key. Swedish lives in Resources/SharedResource.sv.resx.
/// </summary>
public sealed class SharedResource;
