namespace JeffreyPalermo.Core.Content;

/// <summary>Thrown when content breaks an invariant. Lists every problem, so one build run shows them all.</summary>
public sealed class ContentValidationException : Exception
{
    private const int ErrorsInMessage = 25;

    public ContentValidationException(IReadOnlyList<string> errors)
        : base(Describe(errors)) => Errors = errors;

    public ContentValidationException()
        : this([])
    {
    }

    public ContentValidationException(string message)
        : this([message])
    {
    }

    public ContentValidationException(string message, Exception innerException)
        : base(message, innerException) => Errors = [message];

    public IReadOnlyList<string> Errors { get; }

    private static string Describe(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var shown = string.Join(Environment.NewLine, errors.Take(ErrorsInMessage).Select(e => "  - " + e));
        var more = errors.Count > ErrorsInMessage ? $"{Environment.NewLine}  … and {errors.Count - ErrorsInMessage} more" : string.Empty;
        return $"Content has {errors.Count} problem(s):{Environment.NewLine}{shown}{more}";
    }
}
