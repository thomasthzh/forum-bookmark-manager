namespace Rfbm.Core.Models;

/// <summary>
/// Distinguishes a value which could not be observed from an observed empty
/// value. This prevents a blocked/error page from erasing previously useful
/// data during a recrawl.
/// </summary>
public enum ObservationKind
{
  Unobservable,
  ObservedEmpty,
  ObservedValue
}

public readonly record struct Observed<T>
{
  private readonly T? _value;

  private Observed(ObservationKind kind, T? value)
  {
    Kind = kind;
    _value = value;
  }

  public ObservationKind Kind { get; }

  public bool HasValue => Kind == ObservationKind.ObservedValue;

  public T Value => HasValue
      ? _value!
      : throw new InvalidOperationException($"Observation is {Kind}, not a value.");

  public T? ValueOrDefault => HasValue ? _value : default;

  public static Observed<T> Unobservable() => new(ObservationKind.Unobservable, default);

  public static Observed<T> Empty() => new(ObservationKind.ObservedEmpty, default);

  public static Observed<T> From(T value)
  {
    ArgumentNullException.ThrowIfNull(value);
    return new Observed<T>(ObservationKind.ObservedValue, value);
  }
}
