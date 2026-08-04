using System;

namespace DataChannelUnity.Internal
{
    /// <summary>
    /// 事件派发的异常隔离，**粒度是每订阅者**（SPEC §6 / #38 决议 5）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 硬约束：多播委托按顺序调用，**第一个抛出的订阅者会让后面全都收不到**。
    /// 所以隔离必须做在每个订阅者上：catch、记 Error（带完整栈）、继续下一个。
    /// </para>
    /// <para>
    /// **把整个 <c>Invoke</c> 包在一个 try 里被否掉**，理由不是洁癖：那等于在库内部
    /// 重犯 #30 判定为协议违约的错。订阅者甲抛异常导致订阅者乙**这条消息永远收不到**，
    /// 失败模式正是丢消息，只是位置从队列挪到了分发环节，而且没有任何重传路径。
    /// 「在 reliable 通道上丢消息 = 让 Reliable = true 变成假承诺」这条理由在队列那里
    /// 成立，在这里同样成立。
    /// </para>
    /// <para>
    /// **不自动退订。** 「连抛 N 次就踢掉」的熔断被明确排除 —— 静默改变别人建立的
    /// 订阅关系，比日志刷屏更坏；而刷屏已经由 <see cref="Throttle"/> 处理掉了。
    /// 这与 #45 决议 2 砍掉 pump 无限自愈是同一个形状。
    /// </para>
    /// </remarks>
    internal static class SafeDispatch
    {
        internal static void Invoke(Action handlers, string what)
        {
            if (handlers == null) return;
            var list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action)list[i])(); }
                catch (Exception e) { Report(what, e); }
            }
        }

        internal static void Invoke<T>(Action<T> handlers, T arg, string what)
        {
            if (handlers == null) return;
            var list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<T>)list[i])(arg); }
                catch (Exception e) { Report(what, e); }
            }
        }

        internal static void Invoke<T1, T2>(Action<T1, T2> handlers, T1 a, T2 b, string what)
        {
            if (handlers == null) return;
            var list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<T1, T2>)list[i])(a, b); }
                catch (Exception e) { Report(what, e); }
            }
        }

        /// <summary>单播的 observer 回调。</summary>
        internal static void Observer(Action body, string what)
        {
            try { body(); }
            catch (Exception e) { Report(what, e); }
        }

        /// <summary>
        /// 报一个订阅者异常。节流键是**事件名 + 异常类型** ——
        /// 只按事件名分类的话，一个反复抛同一种异常的订阅者会把同一事件上
        /// 另一个订阅者的另一种异常一并压掉。
        /// </summary>
        internal static void Report(string what, Exception e)
        {
            if (!Throttle.Note(what + "|" + e.GetType().Name, 0, out var suppressed, out var peak))
                return;

            DataChannelLog.Emit(LogLevel.Error,
                what + " 的订阅者抛出异常。**其它订阅者照常收到了这条** —— 隔离是每订阅者的。"
                + "本包不会因此自动退订：静默改掉你建立的订阅关系比日志更坏。"
                + Throttle.SuppressedSuffix(suppressed, peak, ""),
                e);
        }
    }
}
