namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// One collection for every full-system class, so they run one after another: each starts <c>dotnet</c> commands
/// (publish, run) that build the same projects, and two at once would write the same build output.
/// </summary>
internal static class FullSystem
{
    public const string Collection = "full-system";
}
