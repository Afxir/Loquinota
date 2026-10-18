namespace Loquinota.Helpers;

/// <summary>
/// Exception helpers used by the audio engine when reporting inner failures.
/// </summary>
public static class ExceptionExtensions
{
    /// <summary>
    /// Returns the most useful exception for logging: the single inner exception of an
    /// AggregateException, or the deepest InnerException of a wrapped exception chain.
    /// </summary>
    public static Exception Flatten(this Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var flattened = aggregate.Flatten();
            return flattened.InnerExceptions.Count == 1 ? flattened.InnerExceptions[0] : flattened;
        }

        var current = exception;
        while (current.InnerException is { } inner && !ReferenceEquals(inner, current))
        {
            current = inner;
        }

        return current;
    }
}
