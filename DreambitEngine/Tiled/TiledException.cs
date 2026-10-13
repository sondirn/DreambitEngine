using System;

namespace Dreambit.Tiled;

public sealed class TiledException : Exception
{
    public TiledException(string message)
        : base(message)
    {
    }

    public TiledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
