namespace KasaMonitor.Core.Nilm;

/// <summary>
/// An outside source of truth for events already matched to a plug (e.g. a Miele appliance reporting its program
/// phase): a more precise label for the event, or null to keep the plug's alias.
/// </summary>
public interface INilmGroundTruth
{
    /// <summary>The label for an event at <paramref name="tsUtc"/> confirmed by <paramref name="plugId"/>, or null.</summary>
    string? LabelFor(Guid plugId, DateTimeOffset tsUtc);
}
