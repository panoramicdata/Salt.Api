namespace Salt.Api.Exceptions;

/// <summary>
/// The base class for exceptions thrown by Salt.Api.
/// </summary>
public class SaltException : Exception
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// The options are not valid.
/// </summary>
public class SaltConfigurationException : SaltException
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltConfigurationException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltConfigurationException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltConfigurationException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// Login failed, or a request was still refused with HTTP 401 after one re-login and retry.
/// </summary>
/// <remarks>
/// A login that returns HTTP 200 without a token is reported with this exception: rest_cherrypy can answer a
/// failed login with HTTP 200 and an HTML page.
/// </remarks>
public class SaltAuthenticationException : SaltException
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltAuthenticationException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltAuthenticationException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltAuthenticationException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// Read-only mode refused a request. No request was sent.
/// </summary>
public class SaltReadOnlyViolationException : SaltException
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltReadOnlyViolationException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltReadOnlyViolationException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltReadOnlyViolationException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// A patch apply was refused by the client's own guards (missing confirmation, change reference or dry run,
/// a wildcard or too many minions, or an apply already running). No apply request was sent.
/// </summary>
public class SaltPatchGuardException : SaltException
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltPatchGuardException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltPatchGuardException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltPatchGuardException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// The server returned an unexpected HTTP status, including HTTP 429 after every retry was used.
/// </summary>
public class SaltApiException : SaltException
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltApiException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltApiException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltApiException(string message, Exception innerException) : base(message, innerException)
	{
	}

	/// <summary>
	/// Creates an exception for an HTTP status.
	/// </summary>
	public SaltApiException(string message, HttpStatusCode statusCode, string? responseText) : base(message)
	{
		StatusCode = statusCode;
		ResponseText = responseText;
	}

	/// <summary>
	/// The HTTP status code, when there was a response.
	/// </summary>
	public HttpStatusCode? StatusCode { get; }

	/// <summary>
	/// The start of the response body (Salt error bodies are HTML text), when there was one.
	/// </summary>
	public string? ResponseText { get; }
}

/// <summary>
/// A job did not complete before the caller's deadline. <see cref="LastResult"/> holds what had returned so far.
/// </summary>
public class SaltJobTimeoutException : SaltException
{
	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SaltJobTimeoutException()
	{
	}

	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SaltJobTimeoutException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and an inner exception.
	/// </summary>
	public SaltJobTimeoutException(string message, Exception innerException) : base(message, innerException)
	{
	}

	/// <summary>
	/// Creates an exception with the last partial result.
	/// </summary>
	public SaltJobTimeoutException(string message, SaltJobResult? lastResult) : base(message)
	{
		LastResult = lastResult;
	}

	/// <summary>
	/// The last result read before the deadline, when one was read. Minions missing from it had not returned.
	/// </summary>
	public SaltJobResult? LastResult { get; }
}
