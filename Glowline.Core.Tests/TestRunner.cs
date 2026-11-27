using System.Reflection;
using System.Runtime.CompilerServices;

namespace Glowline.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute;

sealed class AssertionException(string message) : Exception(message);

static class Assert
{
    public static void Equal<T>(T expected, T actual,
        [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{expression}: expected <{expected}> but got <{actual}>");
    }

    public static void Near(double expected, double actual, double tolerance = 1e-6,
        [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new AssertionException($"{expression}: expected {expected} ± {tolerance} but got {actual}");
    }

    public static void True(bool condition,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition) throw new AssertionException($"Expected true: {expression}");
    }

    public static void False(bool condition,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (condition) throw new AssertionException($"Expected false: {expression}");
    }

    public static T NotNull<T>(T? value,
        [CallerArgumentExpression(nameof(value))] string? expression = null) where T : class
    {
        return value ?? throw new AssertionException($"Expected a non-null value: {expression}");
    }

    public static void Null(object? value,
        [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (value is not null) throw new AssertionException($"Expected null: {expression} = {value}");
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"Expected {typeof(TException).Name} but {ex.GetType().Name} was thrown");
        }
        throw new AssertionException($"Expected {typeof(TException).Name} but nothing was thrown");
    }
}

static class Program
{
    static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        var failed = 0;
        foreach (var test in tests)
        {
            var name = $"{test.DeclaringType!.Name}.{test.Name}";
            try
            {
                test.Invoke(null, null);
                Console.WriteLine($"  ok    {name}");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}\n        {ex.InnerException.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"{tests.Count} tests passed."
            : $"{failed} of {tests.Count} tests failed.");
        return failed == 0 ? 0 : 1;
    }
}
