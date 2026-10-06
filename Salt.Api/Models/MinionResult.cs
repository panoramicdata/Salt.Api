namespace Salt.Api.Models;

/// <summary>
/// Why a minion's result is a failure.
/// </summary>
public enum MinionFailureKind
{
	/// <summary>The minion succeeded.</summary>
	None,

	/// <summary>
	/// The minion was asked for but is absent from the result: it did not return in time, is down, or has no accepted key.
	/// </summary>
	NotReturned,

	/// <summary>
	/// The minion returned a string where a structured value was expected, for example
	/// <c>'no.such.function' is not available.</c> or <c>Minion did not return. [No response]</c>.
	/// </summary>
	StringResponse,

	/// <summary>The minion returned <c>false</c>.</summary>
	FalseResponse,

	/// <summary>The minion returned a value that could not be read as the expected type.</summary>
	UnexpectedShape,
}

/// <summary>
/// One minion's result: either a value, or a failure with its text. HTTP 200 from Salt does not mean that every
/// minion succeeded, so inspect <see cref="Succeeded"/> before using <see cref="Value"/>.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class MinionResult<T>
{
	internal MinionResult(string minionId, bool succeeded, T? value, MinionFailureKind failureKind, string? failureText)
	{
		MinionId = minionId;
		Succeeded = succeeded;
		Value = value;
		FailureKind = failureKind;
		FailureText = failureText;
	}

	/// <summary>The minion id.</summary>
	public string MinionId { get; }

	/// <summary>Whether the minion returned a value of the expected type.</summary>
	public bool Succeeded { get; }

	/// <summary>The value, when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
	public T? Value { get; }

	/// <summary>Why the result is a failure.</summary>
	public MinionFailureKind FailureKind { get; }

	/// <summary>The failure text, when <see cref="Succeeded"/> is <see langword="false"/>.</summary>
	public string? FailureText { get; }

	/// <inheritdoc/>
	public override string ToString() => Succeeded ? $"{MinionId}: {Value}" : $"{MinionId}: failed ({FailureKind}): {FailureText}";
}

/// <summary>
/// Creates <see cref="MinionResult{T}"/> values.
/// </summary>
public static class MinionResult
{
	/// <summary>Creates a successful result.</summary>
	public static MinionResult<T> Success<T>(string minionId, T value)
		=> new(minionId, true, value, MinionFailureKind.None, null);

	/// <summary>Creates a failed result.</summary>
	public static MinionResult<T> Failure<T>(string minionId, MinionFailureKind kind, string failureText)
		=> new(minionId, false, default, kind, failureText);
}

/// <summary>
/// Per-minion results, keyed by minion id.
/// </summary>
/// <remarks>
/// <see cref="AllSucceeded"/> is <see langword="false"/> for an empty set: a target that matched no minion is never
/// reported as "all clean". Check <see cref="NoMinionsMatched"/> to tell that case apart.
/// </remarks>
/// <typeparam name="T">The value type.</typeparam>
public sealed class MinionResultDictionary<T> : IReadOnlyDictionary<string, MinionResult<T>>
{
	private readonly Dictionary<string, MinionResult<T>> _results;

	/// <summary>
	/// Creates a result set.
	/// </summary>
	/// <param name="results">The results.</param>
	/// <param name="noMinionsMatched">Whether the server returned no minion at all.</param>
	public MinionResultDictionary(IEnumerable<MinionResult<T>> results, bool noMinionsMatched)
	{
		ArgumentNullException.ThrowIfNull(results);
		_results = results.ToDictionary(r => r.MinionId, StringComparer.Ordinal);
		NoMinionsMatched = noMinionsMatched;
	}

	/// <summary>
	/// Whether the server returned no minion at all (Salt answers a target that matches nothing with an empty object).
	/// For a list target, the ids asked for are still present, as <see cref="MinionFailureKind.NotReturned"/> failures.
	/// </summary>
	public bool NoMinionsMatched { get; }

	/// <summary>Whether there is at least one result and every result succeeded.</summary>
	public bool AllSucceeded => _results.Count > 0 && _results.Values.All(r => r.Succeeded);

	/// <summary>The successful results.</summary>
	public IEnumerable<MinionResult<T>> Successes => _results.Values.Where(r => r.Succeeded);

	/// <summary>The failed results.</summary>
	public IEnumerable<MinionResult<T>> Failures => _results.Values.Where(r => !r.Succeeded);

	/// <inheritdoc/>
	public MinionResult<T> this[string key] => _results[key];

	/// <inheritdoc/>
	public IEnumerable<string> Keys => _results.Keys;

	/// <inheritdoc/>
	public IEnumerable<MinionResult<T>> Values => _results.Values;

	/// <inheritdoc/>
	public int Count => _results.Count;

	/// <inheritdoc/>
	public bool ContainsKey(string key) => _results.ContainsKey(key);

	/// <inheritdoc/>
	public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MinionResult<T> value)
		=> _results.TryGetValue(key, out value);

	/// <inheritdoc/>
	public IEnumerator<KeyValuePair<string, MinionResult<T>>> GetEnumerator() => _results.GetEnumerator();

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
