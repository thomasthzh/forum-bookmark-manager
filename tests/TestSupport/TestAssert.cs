namespace Rfbm.Tests;

public static class TestAssert
{
  public static void Equal<T>(T expected, T actual, string? message = null)
  {
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
      throw new InvalidOperationException(message ?? $"Expected '{expected}', got '{actual}'.");
    }
  }

  public static T NotNull<T>(T? value, string? message = null)
      where T : class
  {
    if (value is null)
    {
      throw new InvalidOperationException(message ?? "Expected a non-null value.");
    }

    return value;
  }

  public static void Throws<TException>(Action action)
      where TException : Exception
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
      throw new InvalidOperationException(
          $"Expected {typeof(TException).Name}, got {ex.GetType().Name}.",
          ex);
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
  }
}
