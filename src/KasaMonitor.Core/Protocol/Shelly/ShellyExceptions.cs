namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>The device answered 401: no password is configured, or the configured one was rejected.</summary>
public sealed class ShellyAuthenticationException : Exception
{
    public bool PasswordMissing { get; }

    public ShellyAuthenticationException(string message, bool passwordMissing) : base(message) => PasswordMissing = passwordMissing;
}

/// <summary>A JSON-RPC call failed (HTTP error or an RPC error object such as "method not found").</summary>
public sealed class ShellyRpcException : Exception
{
    /// <summary>HTTP status, or the RPC error code of a WebSocket answer (401 = authentication needed).</summary>
    public int? HttpStatus { get; }
    public string Method { get; }

    /// <summary>The device's error message as received (a JSON challenge for a WebSocket 401).</summary>
    public string Detail { get; }

    public ShellyRpcException(string method, string message, int? httpStatus = null) : base($"{method}: {message}")
    {
        Method = method;
        HttpStatus = httpStatus;
        Detail = message;
    }
}

/// <summary>EM1Data.GetData still had records after the page limit: the window was not read completely and must not be stored.</summary>
public sealed class ShellyIncompleteDataException : Exception
{
    public ShellyIncompleteDataException(string message) : base(message) { }
}
