using System;

namespace DataChannelUnity
{
    /// <summary>
    /// 原生层失败。**托管侧的误用不走这里** —— 那些抛标准 .NET 异常
    /// （<see cref="ArgumentNullException"/> / <see cref="ArgumentException"/> /
    /// <see cref="InvalidOperationException"/> / <see cref="ObjectDisposedException"/>）。
    /// 两个空间刻意分开，别把标准异常塞进来。
    /// </summary>
    public sealed class DataChannelException : Exception
    {
        /// <summary>失败分类。**用它承接控制流。**</summary>
        public DataChannelError ErrorCode { get; }

        /// <summary>
        /// ABI 返回的原始数值。**仅用于诊断与 bug 报告，不要用于控制流。**
        /// </summary>
        /// <remarks>
        /// 保留它是错误码独立编号的**必要配套**：独立编号的全部价值就是「看到不认识的码
        /// 能判断是上游漏出来的」，若公开面只有枚举，漏出来的码会变成
        /// <see cref="DataChannelError.Unknown"/> 而丢失数值本身。
        /// </remarks>
        public int RawCode { get; }

        public DataChannelException(string message)
            : this(message, DataChannelError.Failure, (int)DataChannelError.Failure)
        {
        }

        public DataChannelException(string message, DataChannelError errorCode, int rawCode)
            : base(message)
        {
            ErrorCode = errorCode;
            RawCode = rawCode;
        }

        public DataChannelException(string message, Exception inner)
            : base(message, inner)
        {
            ErrorCode = DataChannelError.Failure;
            RawCode = (int)DataChannelError.Failure;
        }
    }
}
