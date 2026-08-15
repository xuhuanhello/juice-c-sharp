using System.Text;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 一局台球在屏幕上要显示的全部：阶段、谁的回合、两组各剩几颗、两个倒计时、终局横幅、
    /// 两个 rematch ready。
    ///
    /// 形状照 <see cref="RoomPanel"/> 与 <see cref="ConnectionDiagnosticsHud"/>：**IMGUI，不建
    /// Canvas 层级**。手势那半在 <see cref="BilliardsTouchControls"/>。
    ///
    /// 下面每一项都已经在 <see cref="BilliardsGame"/> 的公开面上，这块一行状态都不存 ——
    /// 剩几颗由落袋掩码派生（<see cref="BilliardsState.Remaining"/>），存一份就会与掩码不一致。
    ///
    /// ## 两个倒计时刻意是本地的
    ///
    /// 60 s 回合超时由 host 计时（#132），30 s 留座由 host 计时（#134），但**秒数从不上线**：
    /// 一个每 tick 都变的数，代价会超过整条状态消息。所以这块自己起钟 ——
    /// 回合钟在收到 Aim/Break 消息那一刻归零（与 host 的 `PublishState` 同一个时刻），
    /// 重连钟直接读 <see cref="BilliardsGame.ReconnectSecondsRemaining"/>（那已经是本地跑的）。
    ///
    /// 30 秒的沉默与「卡死了」不可区分 —— 那正是 #134 要这个数可见的理由。
    ///
    /// ## 终局是两句不同的话
    ///
    /// 一局结束可能是**有人赢了**，也可能是**掉线的人没回来**。`Abandoned` 且没有赢家**不是输**。
    /// 告诉一个玩家他输掉了一局其实作废了的球，比什么都不说更糟，所以两条分支各写各的话，
    /// 中间没有共用的句子可以被误读。
    /// </summary>
    public sealed class BilliardsHud : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("留空则自己找场景里的那一个。")]
        private BilliardsGame _game;

        [SerializeField]
        [Tooltip("回合倒计时的总秒数。只用于显示；判超时的钟在 host 上。")]
        private float _turnSeconds = BilliardsRules.TurnTimeoutSeconds;

        private GUIStyle _label;
        private GUIStyle _banner;
        private readonly StringBuilder _sb = new StringBuilder();

        /// <summary>本地回合钟。收到 Aim/Break 那一刻归零，与 host 的 `_turnElapsed` 对齐。</summary>
        private float _turnElapsed;

        /// <summary>
        /// 「对手已回来」那句提示的剩余显示秒数。#134 要这条：否则断线的人回来后局面变化
        /// 会被读成 bug。
        /// </summary>
        private float _reconnectedNoticeSeconds;

        private const float ReconnectedNoticeDuration = 4f;

        private void Awake()
        {
            if (_game == null)
                _game = FindObjectOfType<BilliardsGame>();
        }

        private void OnEnable()
        {
            if (_game != null)
                _game.StateChanged += OnStateChanged;
        }

        private void OnDisable()
        {
            if (_game != null)
                _game.StateChanged -= OnStateChanged;
        }

        private void OnStateChanged(BilliardsState state)
        {
            // host 的 `PublishState` 在**每一条** Aim/Break 消息上把 `_turnElapsed` 归零，
            // 所以这里也在每一条上归零，而不是只在「换了回合」时 —— 那两个条件不一样：
            // 等重连的标志在 Aim 阶段立起或落下时，host 会带着同一个阶段与同一个座位再发一次，
            // 于是它的钟重置了而屏幕上的没有，倒计时会比真的少。
            if (state.Phase == BilliardsPhase.Aim || state.Phase == BilliardsPhase.Break)
                _turnElapsed = 0f;

            // 标志由 1 落到 0 且这一局没作废＝对手回来了。这是唯一能认出这件事的边沿：
            // host 只把标志清掉（`ClearReconnectWaitIfDone`），没有单独的「回来了」消息。
            bool nowWaiting = state.HasFlag(BilliardsFlags.AwaitingReconnect);
            if (_wasWaiting && !nowWaiting && !state.HasFlag(BilliardsFlags.Abandoned))
                _reconnectedNoticeSeconds = ReconnectedNoticeDuration;

            _wasWaiting = nowWaiting;
        }

        private bool _wasWaiting;

        private void Update()
        {
            if (_game == null)
                return;

            BilliardsState state = _game.State;
            bool running = state.Phase == BilliardsPhase.Aim || state.Phase == BilliardsPhase.Break;

            // 等重连时 host 把回合钟停住（一个玩家不该因为别人的连接丢掉自己的回合），
            // 所以这里也停。
            if (running && !state.HasFlag(BilliardsFlags.AwaitingReconnect))
                _turnElapsed += Time.deltaTime;

            if (_reconnectedNoticeSeconds > 0f)
                _reconnectedNoticeSeconds -= Time.deltaTime;
        }

        private void OnGUI()
        {
            if (_game == null)
                return;

            _label ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white }
            };

            _banner ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };

            DrawStatusPanel();
            DrawBanner();
        }

        /// <summary>
        /// 左下那块读数。左上是 <see cref="RoomPanel"/>、右上是
        /// <see cref="ConnectionDiagnosticsHud"/>，三块互不重叠。
        /// </summary>
        private void DrawStatusPanel()
        {
            BilliardsState state = _game.State;

            _sb.Clear();
            _sb.AppendLine($"阶段 {PhaseText(state.Phase)}");

            if (_game.LocalSeat == BilliardsRules.SeatNone)
            {
                _sb.AppendLine("还没有座位 —— 等入座");
            }
            else
            {
                _sb.AppendLine($"我是座位 {_game.LocalSeat}（{GroupText(_game.LocalSeat)}）");

                bool myTurn = state.TurnSeat == _game.LocalSeat;
                string turn = state.TurnSeat == BilliardsRules.SeatNone
                    ? "没有人的回合"
                    : myTurn ? "▶ 我的回合" : $"对手的回合（座位 {state.TurnSeat}）";
                _sb.AppendLine(turn);
            }

            _sb.AppendLine($"实色 1–7 剩 {state.Remaining(BilliardsRules.SeatHost)}   " +
                           $"花色 9–15 剩 {state.Remaining(BilliardsRules.SeatClient)}   " +
                           $"8 号{(state.EightStillUp ? "在台上" : "已落袋")}");

            if (state.HasFlag(BilliardsFlags.BallInHand))
            {
                _sb.AppendLine(state.TurnSeat == _game.LocalSeat
                    ? "自由摆球：点一下放白球，然后照常拖动瞄准"
                    : "对手拿到自由摆球");
            }

            bool waiting = state.HasFlag(BilliardsFlags.AwaitingReconnect);
            if (waiting)
            {
                _sb.AppendLine($"⏸ 对手掉线了，等重连 {_game.ReconnectSecondsRemaining:F0}s" +
                               "（输入已冻住）");
            }
            else if (state.Phase == BilliardsPhase.Aim || state.Phase == BilliardsPhase.Break)
            {
                float left = Mathf.Max(0f, _turnSeconds - _turnElapsed);
                _sb.AppendLine($"回合倒计时 {left:F0}s（超时只换人，不算犯规）");
            }

            if (_reconnectedNoticeSeconds > 0f)
                _sb.AppendLine("✔ 对手已回来，局面完整");

            string text = _sb.ToString();
            var size = _label.CalcSize(new GUIContent(text));
            const float pad = 8f;
            float w = Mathf.Max(size.x + pad * 2f, 320f);
            float h = size.y + pad * 2f;
            var rect = new Rect(10f, Screen.height - h - 10f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(rect.x + pad, rect.y + pad, size.x, size.y), text, _label);
        }

        /// <summary>
        /// 终局横幅与 rematch。**作废与输赢是两条互不共用句子的分支** —— 见类注释。
        /// </summary>
        private void DrawBanner()
        {
            BilliardsState state = _game.State;
            bool over = state.Phase == BilliardsPhase.GameOver ||
                        state.Phase == BilliardsPhase.RematchPending;

            // 超窗口回来的那一端：它没收到 GameOver（那时它不在线上），只被告知这一局作废。
            if (!over && _game.LocalGameVoided)
            {
                DrawBannerBox("你离开太久，那一局已经作废了。\n不是输 —— 那一局没有胜负。", null);
                return;
            }

            if (!over)
                return;

            DrawBannerBox(Headline(state.HasFlag(BilliardsFlags.Abandoned), state.Winner,
                _game.LocalSeat), RematchText(state));
        }

        /// <summary>
        /// 终局那句话。**纯函数**，因为「作废与输赢是两句分开的话」是这块唯一一条能被写错而
        /// 编译器不管的要求 —— 抽出来它才能被直接核，而不是只能靠人看一眼截图。
        ///
        /// 要守的是**作废那一支从不说「你输了」**，并且两支不共用句子（共用的那半句一旦被改动，
        /// 两种局面就会开始用同一种说法）。它说的是「不计输赢」—— 出现「输赢」二字是在否定它们，
        /// 与断言一次失败是相反的意思。
        /// </summary>
        public static string Headline(bool abandoned, byte winner, int localSeat)
        {
            if (abandoned)
                return "这一局作废：对手没在 30 秒内回来。\n没有胜负，不计输赢。";

            if (winner == BilliardsRules.SeatNone)
                // 到不了这里 —— GameOver 要么有赢家要么带 Abandoned。说出来而不是显示一句空话。
                return "这一局结束了，但既没有赢家也没标作废 —— 状态不一致，请看日志。";

            return winner == localSeat
                ? $"你赢了这一局（座位 {winner}）。"
                : $"你输了这一局，座位 {winner} 赢。";
        }

        /// <summary>两个 ready 位双方都看得见（#132）：谁同意了、还差谁。</summary>
        private string RematchText(BilliardsState state)
        {
            bool hostReady = state.HasFlag(BilliardsFlags.HostReady);
            bool clientReady = state.HasFlag(BilliardsFlags.ClientReady);

            string mine = _game.LocalSeat == BilliardsRules.SeatHost ? Mark(hostReady) : Mark(clientReady);
            string theirs = _game.LocalSeat == BilliardsRules.SeatHost ? Mark(clientReady) : Mark(hostReady);

            return $"再来一局：我 {mine}   对手 {theirs}   （双方都同意才开始，上一局的输家开球）";
        }

        private static string Mark(bool ready) => ready ? "✔ 已同意" : "… 未同意";

        private void DrawBannerBox(string headline, string rematch)
        {
            const float w = 560f;
            float h = rematch == null ? 110f : 170f;
            var rect = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.28f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;

            GUILayout.BeginArea(new Rect(rect.x + 16f, rect.y + 14f, rect.width - 32f, rect.height - 28f));
            GUILayout.Label(headline, _banner);

            if (rematch != null)
            {
                GUILayout.Space(8f);
                GUILayout.Label(rematch, _label);

                bool alreadyReady = _game.LocalSeat == BilliardsRules.SeatHost
                    ? _game.State.HasFlag(BilliardsFlags.HostReady)
                    : _game.State.HasFlag(BilliardsFlags.ClientReady);

                GUI.enabled = _game.LocalSeat != BilliardsRules.SeatNone && !alreadyReady;
                if (GUILayout.Button(alreadyReady ? "已同意，等对手" : "再来一局"))
                    _game.OfferRematch();
                GUI.enabled = true;
            }

            GUILayout.EndArea();
        }

        private static string PhaseText(BilliardsPhase phase) => phase switch
        {
            BilliardsPhase.Lobby => "等人（Lobby）",
            BilliardsPhase.Break => "开球（Break）",
            BilliardsPhase.Aim => "瞄准（Aim）",
            BilliardsPhase.Simulate => "球在跑（Simulate）",
            BilliardsPhase.Resolve => "判定（Resolve）",
            BilliardsPhase.GameOver => "终局（GameOver）",
            BilliardsPhase.RematchPending => "等双方同意（RematchPending）",
            _ => phase.ToString()
        };

        private static string GroupText(int seat) =>
            seat == BilliardsRules.SeatHost ? "实色 1–7" : "花色 9–15";
    }
}
