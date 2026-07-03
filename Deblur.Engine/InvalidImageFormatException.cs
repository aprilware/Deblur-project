namespace Deblur.Engine;

public sealed class InvalidImageFormatException : Exception
{
    public InvalidImageFormatException(string message) : base(message) { }
    public InvalidImageFormatException(string message, Exception inner) : base(message, inner) { }
}
