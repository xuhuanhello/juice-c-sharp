using System;

namespace DataChannelUnity
{
    public sealed class DataChannelException : Exception
    {
        public int ErrorCode { get; }

        public DataChannelException(string message) : base(message)
        {
            ErrorCode = -2;
        }

        public DataChannelException(string message, int errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public DataChannelException(string message, Exception inner) : base(message, inner)
        {
            ErrorCode = -2;
        }
    }
}
