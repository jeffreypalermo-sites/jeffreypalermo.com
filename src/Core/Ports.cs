using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.Core;

/// <summary>Loads the site's content into the domain. The v1 adapter reads <c>content/</c> from the container image.</summary>
public interface ISiteContentSource
{
    /// <exception cref="ContentValidationException">The content breaks an invariant.</exception>
    Task<SiteContent> LoadAsync(CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTime UtcNow { get; }
}
