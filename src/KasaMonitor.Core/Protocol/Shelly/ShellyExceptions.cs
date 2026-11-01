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
    public int? HttpStatus { get; }
    public string Method { get; }

    public ShellyRpcException(string method, string message, int? httpStatus = null) : base($"{method}: {message}")
    {
        Method = method;
        HttpStatus = httpStatus;
    }
}

/// <summary>EM1Data.GetData still had records after the page limit: the window was not read completely and must not be stored.</summary>
public sealed class ShellyIncompleteDataException : Exception
{
    public ShellyIncompleteDataException(string message) : base(message) { }
}
