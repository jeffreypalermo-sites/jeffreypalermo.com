using JeffreyPalermo.Core;

namespace JeffreyPalermo.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
