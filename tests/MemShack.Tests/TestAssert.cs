using System.Collections;
using System.Text.RegularExpressions;

namespace MemShack.Tests;

public static class TestAssert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            Fail(message ?? "Expected condition to be true.");
        }
    }

    public static void True(bool? condition, string? message = null)
        => True(condition == true, message);

    public static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            Fail(message ?? "Expected condition to be false.");
        }
    }

    public static void False(bool? condition, string? message = null)
        => False(condition == true, message);

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            Fail(message ?? $"Expected null, but found '{value}'.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null)
    {
        if (value is null)
        {
            Fail(message ?? "Expected value to be non-null.");
        }

        return value!;
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (expected is string expectedString && actual is string actualString)
        {
            if (!string.Equals(expectedString, actualString, StringComparison.Ordinal))
            {
                Fail(message ?? $"Expected '{expectedString}', but found '{actualString}'.");
            }

            return;
        }

        if (TryAsSequence(expected, actual, out var expectedItems, out var actualItems))
        {
            if (expectedItems.Count != actualItems.Count)
            {
                Fail(message ?? $"Expected sequence length {expectedItems.Count}, but found {actualItems.Count}.");
            }

            for (var index = 0; index < expectedItems.Count; index++)
            {
                if (!object.Equals(expectedItems[index], actualItems[index]))
                {
                    Fail(message ?? $"Sequences differ at index {index}. Expected '{expectedItems[index]}', but found '{actualItems[index]}'.");
                }
            }

            return;
        }

        if (!object.Equals(expected, actual))
        {
            Fail(message ?? $"Expected '{expected}', but found '{actual}'.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (notExpected is string notExpectedString && actual is string actualString)
        {
            if (string.Equals(notExpectedString, actualString, StringComparison.Ordinal))
            {
                Fail(message ?? $"Did not expect '{actualString}'.");
            }

            return;
        }

        if (TryAsSequence(notExpected, actual, out var notExpectedItems, out var actualItems))
        {
            if (notExpectedItems.Count == actualItems.Count &&
                notExpectedItems.Zip(actualItems, static (left, right) => object.Equals(left, right)).All(static equal => equal))
            {
                Fail(message ?? "Did not expect sequences to be equal.");
            }

            return;
        }

        if (object.Equals(notExpected, actual))
        {
            Fail(message ?? $"Did not expect '{actual}'.");
        }
    }

    public static void Empty(IEnumerable collection, string? message = null)
    {
        if (collection.Cast<object?>().Any())
        {
            Fail(message ?? "Expected collection to be empty.");
        }
    }

    public static void NotEmpty(IEnumerable collection, string? message = null)
    {
        if (!collection.Cast<object?>().Any())
        {
            Fail(message ?? "Expected collection to be non-empty.");
        }
    }

    public static void Contains(string expectedSubstring, string? actualString)
        => Contains(expectedSubstring, actualString, StringComparison.Ordinal);

    public static void Contains(string expectedSubstring, string? actualString, StringComparison comparisonType)
    {
        if (actualString is null || actualString.IndexOf(expectedSubstring, comparisonType) < 0)
        {
            Fail($"Expected '{actualString ?? "(null)"}' to contain '{expectedSubstring}'.");
        }
    }

    public static T Contains<T>(T expected, IEnumerable<T> collection)
    {
        foreach (var item in collection)
        {
            if (EqualityComparer<T>.Default.Equals(expected, item))
            {
                return item;
            }
        }

        Fail($"Expected collection to contain '{expected}'.");
        return default!;
    }

    public static T Contains<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        foreach (var item in collection)
        {
            if (predicate(item))
            {
                return item;
            }
        }

        Fail("Expected collection to contain an item matching the predicate.");
        return default!;
    }

    public static void DoesNotContain(string unexpectedSubstring, string? actualString)
        => DoesNotContain(unexpectedSubstring, actualString, StringComparison.Ordinal);

    public static void DoesNotContain(string unexpectedSubstring, string? actualString, StringComparison comparisonType)
    {
        if (actualString is not null && actualString.IndexOf(unexpectedSubstring, comparisonType) >= 0)
        {
            Fail($"Did not expect '{actualString}' to contain '{unexpectedSubstring}'.");
        }
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        foreach (var item in collection)
        {
            if (predicate(item))
            {
                Fail("Did not expect collection to contain an item matching the predicate.");
            }
        }
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var items = collection.ToList();
        if (items.Count != 1)
        {
            Fail($"Expected a single item, but found {items.Count}.");
        }

        return items[0];
    }

    public static T Single<T>(IEnumerable<T> collection, Func<T, bool> predicate)
        => Single(collection.Where(predicate));

    public static void All<T>(IEnumerable<T> collection, Action<T> inspector)
    {
        var index = 0;
        foreach (var item in collection)
        {
            try
            {
                inspector(item);
            }
            catch (Exception exception)
            {
                Fail($"Assertion failed for item at index {index}: {exception.Message}");
            }

            index++;
        }
    }

    public static void StartsWith(string expectedPrefix, string? actualString)
    {
        if (actualString is null || !actualString.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            Fail($"Expected '{actualString ?? "(null)"}' to start with '{expectedPrefix}'.");
        }
    }

    public static void Matches(string pattern, string? actualString)
    {
        if (actualString is null || !Regex.IsMatch(actualString, pattern))
        {
            Fail($"Expected '{actualString ?? "(null)"}' to match regex '{pattern}'.");
        }
    }

    private static void Fail(string message) => throw new Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException(message);

    private static bool TryAsSequence<T>(T expected, T actual, out List<object?> expectedItems, out List<object?> actualItems)
    {
        if (expected is not null &&
            actual is not null &&
            expected is IEnumerable expectedEnumerable &&
            actual is IEnumerable actualEnumerable &&
            expected is not string &&
            actual is not string)
        {
            expectedItems = expectedEnumerable.Cast<object?>().ToList();
            actualItems = actualEnumerable.Cast<object?>().ToList();
            return true;
        }

        expectedItems = [];
        actualItems = [];
        return false;
    }
}
