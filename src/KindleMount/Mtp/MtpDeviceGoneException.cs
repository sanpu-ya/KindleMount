namespace KindleMount.Mtp;

/// <summary>対象デバイスが USB から外れた(あるいは WPD から見えなくなった)ことを示す。</summary>
public sealed class MtpDeviceGoneException : Exception
{
    public MtpDeviceGoneException(string message) : base(message)
    {
    }

    public MtpDeviceGoneException(string message, Exception inner) : base(message, inner)
    {
    }
}
