using System;
using System.Collections.Generic;

namespace Dreambit;

/// <summary>
/// Matches the content cache's trimming, slash normalization, and case-insensitive names,
/// without allocating normalized strings during registry lookups.
/// </summary>
internal sealed class LogicalAssetNameComparer : IEqualityComparer<string>
{
    public static LogicalAssetNameComparer Instance { get; } = new();

    public bool Equals(string? x, string? y)
    {
        if (x is null || y is null)
            return x == y;
        var left = x.AsSpan().Trim();
        var right = y.AsSpan().Trim();
        while (true)
        {
            var leftSlash = left.IndexOfAny('/', '\\');
            var rightSlash = right.IndexOfAny('/', '\\');
            if (leftSlash < 0 || rightSlash < 0)
                return leftSlash == rightSlash && left.Equals(right, StringComparison.OrdinalIgnoreCase);
            if (!left[..leftSlash].Equals(right[..rightSlash], StringComparison.OrdinalIgnoreCase))
                return false;
            left = left[(leftSlash + 1)..];
            right = right[(rightSlash + 1)..];
        }
    }

    public int GetHashCode(string value)
    {
        var remaining = value.AsSpan().Trim();
        var hash = new HashCode();
        int slash;
        while ((slash = remaining.IndexOfAny('/', '\\')) >= 0)
        {
            hash.Add(string.GetHashCode(remaining[..slash], StringComparison.OrdinalIgnoreCase));
            remaining = remaining[(slash + 1)..];
        }
        hash.Add(string.GetHashCode(remaining, StringComparison.OrdinalIgnoreCase));
        return hash.ToHashCode();
    }
}
