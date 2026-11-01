using System.Net;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>
/// One encrypted transport for TP-Link SMART (Tapo) JSON requests: <see cref="KlapTransport"/> and
/// <see cref="AesTransport"/>. TPAP (SPAKE2+, firmware 2025+) can be added later as a further implementation
/// registered under its own <see cref="Protocol"/> name.
/// </summary>
public interface ISmartTransport
{
    /// <summary>Value stored in <c>Plugs.Protocol</c> for devices reached through this transport (see <see cref="PlugProtocols"/>).</summary>
    string Protocol { get; }

    /// <summary>Sends one SMART JSON request and returns the decrypted JSON answer.</summary>
    /// <exception cref="TapoAuthenticationException">The device rejected the credentials.</exception>
    Task<string> SendAsync(IPAddress ip, TapoCredentials credentials, string requestJson, CancellationToken ct);
}

public sealed class TapoAuthenticationException : Exception
{
    public TapoAuthenticationException(string message) : base(message) { }
}
