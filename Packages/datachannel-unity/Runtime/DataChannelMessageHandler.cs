using System;

namespace DataChannelUnity
{
    /// <summary>
    /// DataChannel 消息投递委托。
    /// </summary>
    /// <param name="data">
    /// 本次消息的字节。**只在回调期间有效** —— 它指向 pump 的复用缓冲，
    /// 下一条消息会覆盖它。要保留请自行 <c>ToArray()</c>。
    /// </param>
    /// <remarks>
    /// <para>
    /// 为什么是自定义委托而不是 <c>Action&lt;ReadOnlySpan&lt;byte&gt;&gt;</c>：
    /// Unity 2022.3 是 C# 9，<c>ReadOnlySpan&lt;T&gt;</c> 是 ref struct，
    /// **不能作为泛型类型实参**（<c>allows ref struct</c> 要 C# 13），那个写法编译不过。
    /// </para>
    /// <para>
    /// 为什么是 Span 而不是 <c>byte[]</c> 或 <c>ArraySegment&lt;byte&gt;</c>：
    /// <c>ArraySegment</c> 能零拷贝直喂 FishNet 之类的 transport，诱惑是真的，但它把
    /// 复用缓冲的**可写数组**交出去 —— 应用存下来跨帧用，下一帧被别的消息覆盖，
    /// 就是**运行期静默数据损坏**，错误现场还在下一帧。Span 把同一个错变成**编译错误**。
    /// </para>
    /// </remarks>
    public delegate void DataChannelMessageHandler(ReadOnlySpan<byte> data);
}
