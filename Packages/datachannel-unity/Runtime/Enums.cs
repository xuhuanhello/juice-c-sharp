namespace DataChannelUnity
{
    /// <summary>
    /// PeerConnection 连接状态。
    /// </summary>
    /// <remarks>
    /// <see cref="Unknown"/> 对应上游新增了我们还不认识的成员。库**绝不**因此抛异常，
    /// 也不丢掉该事件（那会让应用永远停在旧状态），更不会把它冒充成某个既有成员。
    /// </remarks>
    public enum ConnectionState
    {
        Unknown = -1,
        New = 0,
        Connecting = 1,
        Connected = 2,
        Disconnected = 3,
        Failed = 4,
        Closed = 5
    }

    /// <summary>ICE 候选收集状态。<see cref="Unknown"/> 语义同 <see cref="ConnectionState"/>。</summary>
    public enum GatheringState
    {
        Unknown = -1,
        New = 0,
        InProgress = 1,
        Complete = 2
    }

    public enum IceTransportPolicy
    {
        All = 0,
        RelayOnly = 1
    }

    public enum LogLevel
    {
        None = 0,
        Fatal = 1,
        Error = 2,
        Warning = 3,
        Info = 4,
        Debug = 5,
        Verbose = 6
    }

    /// <summary>
    /// 原生层失败的分类。**这是传输层管道语义，不含应用层含义。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 枚举值刻意等于 ABI 的数值，且**独立编号**、不与上游 <c>RTC_ERR_*</c> 逐值相同：
    /// 一旦有人在原生侧写出直接透传上游错误码的代码，数值相同的话会返回一个
    /// 「长得完全合法」的码并被静默当真；独立编号下它是未定义的码，会落到
    /// <see cref="Unknown"/>，而原始数值仍由 <see cref="DataChannelException.RawCode"/> 带出。
    /// </para>
    /// <para>
    /// 用 <see cref="DataChannelException.ErrorCode"/> 承接控制流；
    /// <see cref="DataChannelException.RawCode"/> **仅用于诊断与 bug 报告**。
    /// </para>
    /// </remarks>
    public enum DataChannelError
    {
        /// <summary>不认识的错误码。真实数值见 <see cref="DataChannelException.RawCode"/>。</summary>
        Unknown = int.MinValue,

        /// <summary>调用方传错了东西，通常可自助修复。</summary>
        Invalid = -101,

        /// <summary>运行期失败。</summary>
        Failure = -102,

        /// <summary>此刻没有可返回的东西。</summary>
        NotAvailable = -103,

        /// <summary>调用方缓冲不足；所需长度已由 out 参数带出，扩容重试是幂等的。</summary>
        TooSmall = -104,

        /// <summary>上游失败但无法归类。**刻意不压平成 <see cref="Failure"/>**。</summary>
        UpstreamUnknown = -105
    }
}
