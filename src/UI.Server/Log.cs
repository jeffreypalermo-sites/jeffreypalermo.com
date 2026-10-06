namespace JeffreyPalermo.UI.Server;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded content {Version}: {Posts} posts, {Attachments} attachments")]
    public static partial void ContentLoaded(ILogger logger, string version, int posts, int attachments);
}
