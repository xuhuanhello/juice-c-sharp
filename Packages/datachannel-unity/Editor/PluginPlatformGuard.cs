using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace DataChannelUnity.Editor
{
    /// <summary>
    /// 目标平台没有本包的原生插件时，**在构建期终止构建**（决议 #53）。
    ///
    /// <para>
    /// 这是**分批入库的配套**，不是可选的贴心功能。分批意味着某些平台暂时没有
    /// 二进制；没有这一关，默认行为是：Unity 构建**成功**，然后在最终用户的设备上
    /// 第一次 P/Invoke 时抛 <c>DllNotFoundException</c> —— 而 #49 已实测确认，
    /// 那个现象与「文件压根不存在」不可区分（这里它字面上就是不存在），Console
    /// 一条日志都没有。没有这一关，分批就是在拿采用者当探测器。
    /// </para>
    ///
    /// <para>
    /// **判据用 Unity 自己的兼容性解析**（<c>PluginImporter.GetCompatibleWithPlatform</c>），
    /// 不是自己拼路径去猜文件在不在。理由：真正决定「插件会不会进包」的就是这套
    /// 解析。自己猜路径的话，路径对了而 <c>.meta</c> 的平台开关写错时，这一关会
    /// 放行 —— 而那恰恰是 #49 指出的、最容易静默失效的一类错误。
    /// </para>
    /// </summary>
    public sealed class PluginPlatformGuard : IPreprocessBuildWithReport
    {
        /// <summary>尽量早跑：晚于它的处理器可能已经花了很久。</summary>
        public int callbackOrder => -1000;

        /// <summary>
        /// 按**产物文件名**识别本包的插件，不按路径。
        ///
        /// <para>
        /// 起初用的是路径片段 <c>"datachannel-unity/Plugins/"</c>，**它一个都匹配不到**：
        /// 包被 Unity 装载后，资产路径用的是**包名**
        /// （<c>Packages/com.xuhuanhello.datachannel/Plugins/…</c>），不是磁盘上的
        /// 文件夹名。后果比失效更糟 —— 匹配到 0 个插件时，这一关会对**所有**平台
        /// 抛异常，包括确实有二进制的那个。
        /// </para>
        ///
        /// <para>
        /// 文件名是 SPEC §8 钉死的，也是 <c>DllImport</c> 真正绑定的东西，与包被放在
        /// <c>Packages/</c> 还是嵌进 <c>Assets/</c> 无关。
        /// </para>
        /// </summary>
        static readonly string[] PluginFileNames =
        {
            "datachannel_unity",     // Windows .dll / macOS .bundle
            "libdatachannel_unity",  // Linux/Android .so、iOS/WebGL .a
        };

        static bool IsOurs(PluginImporter importer)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(importer.assetPath);
            foreach (var candidate in PluginFileNames)
                if (name == candidate) return true;
            return false;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            var target = report.summary.platform;

            var ours = PluginImporter.GetAllImporters()
                .Where(p => p != null && p.isNativePlugin && IsOurs(p))
                .ToArray();

            if (ours.Any(p => p.GetCompatibleWithPlatform(target)))
                return;

            var landed = ours
                .Select(p => p.assetPath.Replace('\\', '/'))
                .OrderBy(p => p)
                .ToArray();

            var message = new StringBuilder();
            message.Append("com.xuhuanhello.datachannel ships no native plugin for build target '")
                   .Append(target)
                   .Append("'. The build is stopped here on purpose.\n\n")
                   .Append("Building anyway would succeed and then fail on the end user's device with a ")
                   .Append("DllNotFoundException that is indistinguishable from the file simply not existing ")
                   .Append("(it does not, for this platform).\n\n");

            if (landed.Length == 0)
            {
                message.Append("This package currently ships NO platform binaries at all. ")
                       .Append("They are added in batches as each platform passes its verification; ")
                       .Append("see the support table in the package README.\n");
            }
            else
            {
                message.Append("Platforms this package currently ships:\n");
                foreach (var p in landed) message.Append("  - ").Append(p).Append('\n');
                message.Append("\nSee the support table in the package README.\n");
            }

            throw new BuildFailedException(message.ToString());
        }
    }
}
