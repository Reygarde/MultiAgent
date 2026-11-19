using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

internal static class ExactSourceReplacement
{
    public static int FindUnique(string source, string original,
        string failure = "The original text must match exactly once; no approximate patch was applied.")
    {
        if (string.IsNullOrWhiteSpace(original)) throw new StudioValidationException(failure);
        var index = source.IndexOf(original, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(original, index + 1, StringComparison.Ordinal) >= 0)
            throw new StudioValidationException(failure);
        return index;
    }
}
