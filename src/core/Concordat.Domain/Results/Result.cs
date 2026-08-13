using System.Diagnostics.CodeAnalysis;

namespace Concordat.Domain.Results;

/// <summary>
/// The outcome of a domain operation that returns no value.
/// </summary>
/// <remarks>
/// <para>
/// Declared in the domain rather than the application layer because the domain must be able
/// to return it and cannot reference anything above it (DESIGN §8 dependency rule).
/// </para>
/// <para>
/// A sealed class rather than a struct on purpose: <c>default(Result)</c> on a struct would be
/// a valid-looking instance that is neither success nor failure, and no analyzer catches it.
/// As a class, <c>default</c> is <see langword="null"/> and nullable reference types flag every
/// use site.
/// </para>
/// </remarks>
public sealed class Result
{
    private Result(bool isSuccess, DomainError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed. The inverse of <see cref="IsSuccess"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure, or <see langword="null"/> when the operation succeeded.</summary>
    public DomainError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    /// <returns>A successful <see cref="Result"/>.</returns>
    public static Result Success() => new(true, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The rule that was violated.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Failure(DomainError error) => new(false, error);

    /// <summary>Creates a failed result from a code and message.</summary>
    /// <param name="code">A constant from <see cref="ConcordatCodes"/>.</param>
    /// <param name="message">A human-readable explanation.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Failure(string code, string message) =>
        new(false, new DomainError(code, message));
}

/// <summary>
/// The outcome of a domain operation that returns a value on success.
/// </summary>
/// <typeparam name="T">The value produced on success.</typeparam>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification =
        "Result<T>.Success(value) is the readable form and the near-universal convention for " +
        "this pattern. The alternative, Result.Success<T>(value), forces an explicit type " +
        "argument on every failure site where it cannot be inferred.")]
public sealed class Result<T>
    where T : notnull
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, DomainError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed. The inverse of <see cref="IsSuccess"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure, or <see langword="null"/> when the operation succeeded.</summary>
    public DomainError? Error { get; }

    /// <summary>The produced value.</summary>
    /// <exception cref="InvalidOperationException">The operation failed.</exception>
    public T Value => IsSuccess && _value is not null
        ? _value
        : throw new InvalidOperationException(
            $"No value: the operation failed with '{Error?.Code}'.");

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The produced value.</param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The rule that was violated.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static Result<T> Failure(DomainError error) => new(false, default, error);

    /// <summary>Creates a failed result from a code and message.</summary>
    /// <param name="code">A constant from <see cref="ConcordatCodes"/>.</param>
    /// <param name="message">A human-readable explanation.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static Result<T> Failure(string code, string message) =>
        new(false, default, new DomainError(code, message));
}
