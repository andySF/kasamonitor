namespace KasaMonitor.Core.Protocol;

public sealed class KasaProtocolException : Exception
{
    public int ErrCode { get; }
    public string? Operation { get; }

    public KasaProtocolException(string message, int errCode = 0, string? operation = null)
        : base(message)
    {
        ErrCode = errCode;
        Operation = operation;
    }
}
