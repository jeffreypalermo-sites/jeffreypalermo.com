namespace JeffreyPalermo.UI.Server;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded content {Version}: {Posts} posts, {Attachments} attachments")]
    public static partial void ContentLoaded(ILogger logger, string version, int posts, int attachments);

    [LoggerMessage(Level = LogLevel.Information, Message = "/_build answers the facts the Build wrote about release {Version}")]
    public static partial void BuildFactsLoaded(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "/_build answers the version alone: no facts of release {Version} at {Path}")]
    public static partial void NoBuildFacts(ILogger logger, string version, string path);
}
