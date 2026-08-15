namespace DataChannelUnity.Example
{
    /// <summary>
    /// 传输层向游戏层问「这个座位还留着吗」的那一道窄口（#134）。
    ///
    /// ## 为什么必须是一道口子，而不是让 Transport 自己管
    ///
    /// #134 定了留座逻辑**全在游戏层**：Transport 断了就删行、报 <c>Stopped</c>，重连是一条
    /// 全新连接（新 connectionId，与 #120 的「永不复用」一致）。让 Transport 自己维持「等
    /// 重连」态会在 server 侧造出一套 client 侧没有的状态机 —— 那**正好命中 #133 那条推翻
    /// 条件**（两侧不再共享同一套 <c>Peer</c> 生命周期），于是「不拆成 client/server 两个类」
    /// 的前提就没了。
    ///
    /// 但满员拦截**必须**在信令层（#134 事实 4：掉线时 <c>_serverPeers</c> 那行被删，人数从
    /// 2 掉回 1，拦截当场放行 —— 座位在传输层根本没被留住）。所以 Transport 需要知道两件事，
    /// 而且只这两件：**留了几个座**，以及**手里这个令牌能不能取回其中一个**。它不解析令牌、
    /// 不认识座位号、不存任何等待态。
    ///
    /// ## 令牌对 Transport 是不透明字符串
    ///
    /// 谁签发、里面是什么、怎么比，全归游戏层。Transport 只负责把它从 <c>description</c> 的
    /// payload 里取出来转上去（host 侧），或者取出来放进去（client 侧）。
    /// </summary>
    public interface ISeatAuthority
    {
        /// <summary>
        /// 当前被留着等重连的座位数。Transport 把它加进自己的满员计数 —— 这是它唯一需要
        /// 知道的「有人正在回来」。
        /// </summary>
        int HeldSeatCount { get; }

        /// <summary>
        /// 这个令牌能取回一个被留着的座位吗。能则即便满员也放行（那正是留座的意义）。
        /// </summary>
        bool TokenReclaimsSeat(string token);

        /// <summary>
        /// host 侧：某个新连接出示了令牌（可能为空）。在 <c>connectionId</c> 分配之后、
        /// FishNet 的 <c>Started</c> 之前调用，所以游戏层能在座位落定时查到它。
        /// </summary>
        void RemoteTokenPresented(int connectionId, string token);

        /// <summary>
        /// client 侧：本端要出示的令牌，没有则返回 null。Transport 在发 offer 时问一次。
        /// </summary>
        string LocalSeatToken(string roomCode);
    }
}
