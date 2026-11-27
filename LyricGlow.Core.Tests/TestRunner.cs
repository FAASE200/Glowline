using System.Reflection;
using System.Runtime.CompilerServices;

namespace LyricGlow.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute;

sealed class AssertionException(string message) : Exception(message);

static class Assert
{
    public static void Equal<T>(T expected, T actual,
        [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{expression}: se esperaba <{expected}> y se obtuvo <{actual}>");
    }

    public static void Near(double expected, double actual, double tolerance = 1e-6,
        [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new AssertionException($"{expression}: se esperaba {expected} ± {tolerance} y se obtuvo {actual}");
    }

    public static void True(bool condition,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition) throw new AssertionException($"Se esperaba verdadero: {expression}");
    }

    public static void False(bool condition,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (condition) throw new AssertionException($"Se esperaba falso: {expression}");
    }

    public static T NotNull<T>(T? value,
        [CallerArgumentExpression(nameof(value))] string? expression = null) where T : class
    {
        return value ?? throw new AssertionException($"Se esperaba un valor no nulo: {expression}");
    }

    public static void Null(object? value,
        [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (value is not null) throw new AssertionException($"Se esperaba null: {expression} = {value}");
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
            throw new AssertionException($"Se esperaba {typeof(TException).Name} y se lanzó {ex.GetType().Name}");
        }
        throw new AssertionException($"Se esperaba {typeof(TException).Name} y no se lanzó nada");
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
                Console.WriteLine($"  FALLA {name}\n        {ex.InnerException.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"{tests.Count} pruebas correctas."
            : $"{failed} de {tests.Count} pruebas fallaron.");
        return failed == 0 ? 0 : 1;
    }
}
