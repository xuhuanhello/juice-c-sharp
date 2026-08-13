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

    /// <summary>
    /// DataChannel 三态。**这是活查询的结果，不是缓存的事件**。
    /// </summary>
    /// <remarks>
    /// 回调是通知、状态是查询 —— 与浏览器 <c>readyState</c>、libwebrtc <c>state()</c>、
    /// libdatachannel <c>isOpen()</c> 同构。六份参照实现里缓存 open 状态的只有本项目
    /// 旧版一家，而那个缓存正是「一次丢失的通知 = 一个永久卡死的通道」的成因。
    /// </remarks>
    public enum DataChannelState
    {
        Connecting = 0,
        Open = 1,
        Closed = 2
    }

    /// <summary>
    /// 一条已建立的连接实际走的路。
    /// </summary>
    /// <remarks>
    /// **两值，没有 Unknown 档。** 判定只在存在选中候选对时才有意义，而那等价于
    /// 连接已建立；「还没有答案」由 <see cref="PeerConnection.TryGetConnectionPath"/>
    /// 返回 <c>false</c> 表达。多一档 Unknown 就要多定义一套语义、多写一段规格、
    /// 多测一遍，而它永远不可达 —— 同 <see cref="LeakDetectionMode"/> 砍掉中间档
    /// 的取舍（SPEC #45 决议 3）。
    /// </remarks>
    public enum ConnectionPath
    {
        /// <summary>直连。候选对两端都不是中继 —— host、srflx、prflx 都算直连。</summary>
        Direct = 0,

        /// <summary>经 TURN 中继。候选对**任一端**是中继即为此值。</summary>
        Relayed = 1
    }

    public enum IceTransportPolicy
    {
        All = 0,
        RelayOnly = 1
    }

    /// <summary>
    /// 泄漏诊断档位。**两档，不是三档**。
    /// </summary>
    /// <remarks>
    /// <see cref="Enabled"/> 即含创建栈：中间那档「报泄漏但不带栈」被砍掉了
    /// （#45 决议 3）—— 「在哪儿创建的」几乎就是泄漏诊断的全部信息量，
    /// 而多一档就要多定义一套语义、多写一段规格、多测一遍。
    ///
    /// 注意这是**运行时**开关，与终结器本身的**条件编译**是叠加的两层：
    /// Release 构建里终结器根本不存在（有终结器是类型级开销，每个实例都要进
    /// 终结队列、多活一代 GC，运行时开关关不掉），所以在 Release 下把它设成
    /// <see cref="Enabled"/> 也不会有任何报告。
    /// </remarks>
    public enum LeakDetectionMode
    {
        /// <summary>不报告泄漏，也不抓创建栈。</summary>
        Disabled = 0,

        /// <summary>报告泄漏，并附**创建时**的调用栈。</summary>
        Enabled = 1
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
