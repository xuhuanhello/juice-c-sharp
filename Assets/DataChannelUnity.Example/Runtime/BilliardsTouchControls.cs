using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 台球的最小操作层：手势进来，一条 <see cref="BilliardsGame.Shoot"/> 出去。
    ///
    /// 方案与实测数字在 `docs/billiards-touch-controls.md`，Q1–Q3 已定案；这里只落地，不重开那三个问题。
    /// 读数与横幅在 <see cref="BilliardsHud"/>，两块分开是因为一块答「手指在做什么」、
    /// 另一块答「局面是什么」—— 后者一行 `BilliardsGame` 都不碰输入。
    ///
    /// ## 决定形状的那条约束
    ///
    /// 4.7 寸横屏上一颗球只有 8.5 pt（Apple 的最小可点区域是 44 pt），所以**白球没法被直接摸**。
    /// 瞄准因此是间接的：在台面任意位置按下即锚点，往目标的**反方向**拖，拖动的方向给角度、
    /// 长度给力度。抬手出杆。
    ///
    /// ## 力度为什么是拖拽长度而不是滑条
    ///
    /// 距离随力度的**平方**变化（#137 实测 <c>d = 1.262·p²</c>），所以 0–4.0 的线性滑条会把每一次
    /// 走位都挤进它下面三分之一。改成「拖多远、球滚多远」：非线性藏进
    /// <see cref="PowerForDrag"/> 的 <c>√(d/1.262)</c> 里，玩家碰不到。
    ///
    /// ## 开球那一段刻意断掉
    ///
    /// 按屏幕比例最多拖出一个台面长（2.84 m ⇒ power 1.50），而开球要 4.0。所以拖过一个台面长之后
    /// 比例**可见地断掉**，再拖 <see cref="OverRangeDragMetres"/> 米线性升到 4.0，并标「超量程」。
    /// 一局一次，且开球的力度不是走位选择 —— 这是唯一一处手感不一致是诚实的地方。
    ///
    /// **判据用拖拽长度，不用「手指越过了台面边界」**，虽然文档是按后者描述的：越界那条判据
    /// 与锚点在哪有关，于是同一个手势在台面中央与靠库边给出不同力度，而力度控件最不能有的
    /// 就是这个。长度判据与锚点、方向都无关，且 2.84 m 恰好就是文档写的 power 1.50 那条上限。
    ///
    /// ## 全部是本地意图
    ///
    /// 瞄准与摆球从不过网络（#127）。抬手那一刻才有五个 float 走 Reliable 出去，
    /// 在那之前对手什么都看不到。
    /// </summary>
    public sealed class BilliardsTouchControls : MonoBehaviour
    {
        #region Tunables

        [SerializeField]
        [Tooltip("留空则自己找场景里的那一个。")]
        private BilliardsGame _game;

        [SerializeField]
        [Tooltip("俯视相机。留空取 Camera.main。")]
        private Camera _camera;

        [SerializeField]
        [Tooltip("滑回锚点这么近算取消（屏幕像素）。也是「点一下」与「拖一下」的分界。")]
        private float _cancelRadiusPixels = 12f;

        [SerializeField]
        [Tooltip("在 Awake 里把朝向锁成横屏。竖屏下半个台子在屏幕外，所以这是硬要求。")]
        private bool _lockLandscape = true;

        #endregion

        #region Constants

        /// <summary>
        /// #137 实测的自由滚系数：<c>d = 1.262·p²</c> 米。系数从 p = 0.5 起稳定到三位小数。
        /// </summary>
        public const float RollCoefficient = 1.262f;

        /// <summary>
        /// 比例段的上限：拖一个台面长，球滚一个台面长。<c>√(2.84/1.262) = 1.50</c>。
        /// </summary>
        public const float ProportionalDragMetres = BilliardsTable.Length;

        /// <summary>
        /// 超量程段的长度。再拖这么多米，力度从比例段末端线性升到
        /// <see cref="BilliardsRules.MaxPower"/>。
        ///
        /// 1.2 m 是**够得着**换来的：总行程 4.04 m，在 667×375 横屏上是 601 px，屏幕对角线 765 px
        /// 之内 —— 一个明显的大动作，但拉得出来。给得更短会让开球轻易误触，更长则拉不满。
        /// </summary>
        public const float OverRangeDragMetres = 1.2f;

        #endregion

        #region State

        private bool _dragging;

        /// <summary>按下那一刻的台面坐标。瞄准与力度都按相对它的位移读。</summary>
        private Vector2 _anchorTable;

        private Vector2 _anchorScreen;
        private Vector2 _currentTable;
        private Vector2 _currentScreen;

        /// <summary>ballInHand 时点下的摆球位（已吸附到合法位），没点过则为 null。</summary>
        private Vector2? _placedCue;

        private GUIStyle _powerStyle;
        private static Texture2D _lineTexture;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_game == null)
                _game = FindObjectOfType<BilliardsGame>();
            if (_camera == null)
                _camera = Camera.main;

            if (_lockLandscape)
                LockLandscape();
        }

        /// <summary>
        /// 运行时锁横屏，不改 `ProjectSettings`。
        ///
        /// 两个理由：朝向是**全工程共享**的设置，锁了它也会锁住 CharacterController 那个示例；
        /// 而 `ProjectSettings.asset` 当前有未提交的改动，内容待确认。在这里锁的范围只在这个场景、
        /// 可逆、不进那份共享文件。
        ///
        /// 留 AutoRotation 而不是钉死 <c>LandscapeLeft</c>：两个横向都放行，于是手机哪边朝上都行，
        /// 只有竖屏被关掉 —— 竖屏下 1.42 m 的可见宽装不下 2.84 m 的台子，半个台面在屏幕外。
        /// 桌面平台上这几个属性是空操作。
        /// </summary>
        private static void LockLandscape()
        {
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;
        }

        #endregion

        #region Power curve

        /// <summary>
        /// 拖拽长度（台面米）到力度。比例段是 <c>√(d/1.262)</c> 的反解，超量程段线性到 4.0。
        /// </summary>
        public static float PowerForDrag(float dragMetres)
        {
            if (dragMetres <= 0f)
                return 0f;

            if (dragMetres <= ProportionalDragMetres)
                return Mathf.Sqrt(dragMetres / RollCoefficient);

            float atBreakpoint = Mathf.Sqrt(ProportionalDragMetres / RollCoefficient);
            float t = Mathf.Clamp01((dragMetres - ProportionalDragMetres) / OverRangeDragMetres);
            return Mathf.Lerp(atBreakpoint, BilliardsRules.MaxPower, t);
        }

        /// <summary>力度到自由滚距离（米）。画「球会停在这」那个圈用的。</summary>
        public static float RollDistanceForPower(float power) => RollCoefficient * power * power;

        /// <summary>拖到这个长度以上就进了超量程段，比例不再成立。</summary>
        public static bool IsOverRange(float dragMetres) => dragMetres > ProportionalDragMetres;

        #endregion

        #region Coordinates

        /// <summary>
        /// 屏幕点到台面坐标。走 <c>ScreenPointToRay</c> 打台面平面，而不是假定相机是俯视正交的：
        /// 相机的摆法是场景构建器的事，这块不该跟着它一起改。
        /// </summary>
        private bool TryScreenToTable(Vector2 screen, out Vector2 table)
        {
            table = default;
            if (_camera == null)
                return false;

            Ray ray = _camera.ScreenPointToRay(screen);
            var plane = new Plane(Vector3.up, new Vector3(0f, BilliardsTable.BallY, 0f));
            if (!plane.Raycast(ray, out float distance))
                return false;

            Vector3 hit = ray.GetPoint(distance);
            table = new Vector2(hit.x, hit.z);
            return true;
        }

        /// <summary>台面坐标到 GUI 坐标（Y 已翻转 —— GUI 的原点在左上）。</summary>
        private Vector2 TableToGui(Vector2 table)
        {
            if (_camera == null)
                return Vector2.zero;

            Vector3 screen = _camera.WorldToScreenPoint(
                new Vector3(table.x, BilliardsTable.BallY, table.y));
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        /// <summary>一屏幕像素等于多少台面米。拖拽长度按台面尺度读，所以这个比例是承重的。</summary>
        private float MetresPerPixel()
        {
            if (_camera == null)
                return 0f;

            // 由相机自己的投影量出来，而不是从 orthographicSize 推：两点各自过一次
            // WorldToScreenPoint，比例就是它们的商，正交还是透视都对。
            Vector3 a = _camera.WorldToScreenPoint(new Vector3(0f, BilliardsTable.BallY, 0f));
            Vector3 b = _camera.WorldToScreenPoint(new Vector3(1f, BilliardsTable.BallY, 0f));
            float pixelsPerMetre = Vector2.Distance(a, b);
            return pixelsPerMetre > 0.0001f ? 1f / pixelsPerMetre : 0f;
        }

        #endregion

        #region Input

        /// <summary>
        /// 现在能不能出杆。四条都不满足就整块冻住 —— 等重连那条是 #134 要的：
        /// 对手在回来的路上，台子是他的。
        /// </summary>
        private bool CanAct()
        {
            if (_game == null || _camera == null)
                return false;
            if (_game.LocalSeat == BilliardsRules.SeatNone)
                return false;
            if (_game.State.HasFlag(BilliardsFlags.AwaitingReconnect))
                return false;
            if (_game.State.Phase != BilliardsPhase.Break && _game.State.Phase != BilliardsPhase.Aim)
                return false;

            return _game.State.TurnSeat == _game.LocalSeat;
        }

        private void Update()
        {
            if (!CanAct())
            {
                // 冻住时把手上的意图丢掉，否则回合回来时会接着一条半途的拖动。
                _dragging = false;
                return;
            }

            if (TryReadPointer(out Vector2 screen, out bool pressed, out bool released))
                DriveGesture(screen, pressed, released);
        }

        /// <summary>
        /// 一个指头或者鼠标左键，取先有的那个。工程走的是旧 Input（`activeInputHandler: 0`），
        /// 所以两条都在 <c>Input</c> 上。
        /// </summary>
        private static bool TryReadPointer(out Vector2 screen, out bool pressed, out bool released)
        {
            screen = default;
            pressed = false;
            released = false;

            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                screen = touch.position;
                pressed = touch.phase == TouchPhase.Began;
                released = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                return true;
            }

            screen = Input.mousePosition;
            pressed = Input.GetMouseButtonDown(0);
            released = Input.GetMouseButtonUp(0);
            return pressed || released || Input.GetMouseButton(0);
        }

        private void DriveGesture(Vector2 screen, bool pressed, bool released)
        {
            if (!TryScreenToTable(screen, out Vector2 table))
                return;

            if (pressed)
            {
                if (IsOverReservedUi(screen))
                    return;

                _dragging = true;
                _anchorScreen = screen;
                _anchorTable = table;
            }

            if (!_dragging)
                return;

            _currentScreen = screen;
            _currentTable = table;

            if (!released)
                return;

            _dragging = false;

            float pixels = Vector2.Distance(screen, _anchorScreen);

            // 短过取消半径的一下是「点」而不是「拖」。ballInHand 时点＝摆球（点第二次就换个位置摆），
            // 否则点＝取消。用同一个阈值分这两件事，是因为它们本来就是同一个手势的两种长度。
            if (pixels < _cancelRadiusPixels)
            {
                if (_game.State.HasFlag(BilliardsFlags.BallInHand))
                    _placedCue = SnapCueSpot(table);

                return;
            }

            ShootFromDrag();
        }

        /// <summary>
        /// `RoomPanel` 占的那块（左上 300×150）不接手势，否则点「断开」会顺手出一杆。
        /// 数值与那块面板重复，是因为 IMGUI 没有可查询的布局；两处要一起改。
        /// </summary>
        private static bool IsOverReservedUi(Vector2 screenPoint)
        {
            var guiPoint = new Vector2(screenPoint.x, Screen.height - screenPoint.y);
            return new Rect(10f, 10f, 300f, 150f).Contains(guiPoint);
        }

        /// <summary>
        /// 摆球位本地先吸附一次，只为了让屏幕上那个白球影子落在它真会去的地方。
        /// host 侧仍会自己吸附一次（#132：**永不驳回**），这只是同一个函数的预览。
        /// </summary>
        private Vector2 SnapCueSpot(Vector2 wanted)
        {
            var occupied = new System.Collections.Generic.List<Vector2>(BilliardsRules.BallCount);
            BilliardsState state = _game.State;
            if (state.BallPositions != null)
            {
                for (int n = 1; n < BilliardsRules.BallCount && n < state.BallPositions.Length; n++)
                {
                    if (!state.IsPocketed(n))
                        occupied.Add(state.BallPositions[n]);
                }
            }

            return BilliardsTable.NearestLegalCueSpot(wanted, occupied);
        }

        /// <summary>
        /// 白球现在在哪，按 <see cref="BilliardsGame.Shoot"/> 用的同一条判别：ballInHand 时是摆球位，
        /// 否则是**快照里**的权威位置。屏幕上那颗滞后约两个 tick 的插值，读它会算错方向（#135 §4）。
        /// </summary>
        private Vector2 CueOrigin()
        {
            BilliardsState state = _game.State;

            if (state.HasFlag(BilliardsFlags.BallInHand))
                return _placedCue ?? DefaultCueSpot(state);

            return state.BallPositions != null &&
                   state.BallPositions.Length > BilliardsRules.CueBall
                ? state.BallPositions[BilliardsRules.CueBall]
                : DefaultCueSpot(state);
        }

        private static Vector2 DefaultCueSpot(BilliardsState state) =>
            state.BallPositions != null && state.BallPositions.Length > BilliardsRules.CueBall &&
            !state.IsPocketed(BilliardsRules.CueBall)
                ? state.BallPositions[BilliardsRules.CueBall]
                : new Vector2(BilliardsTable.HeadSpot.x, BilliardsTable.HeadSpot.z);

        /// <summary>抬手：把手势换成五个 float 交给 <see cref="BilliardsGame.Shoot"/>。</summary>
        private void ShootFromDrag()
        {
            Vector2 drag = _currentTable - _anchorTable;
            if (drag.sqrMagnitude < 1e-8f)
                return;

            // 往目标的反方向拖 —— 像把杆往后拉。
            Vector2 aimDirection = -drag.normalized;
            float power = PowerForDrag(drag.magnitude);

            Vector2 from = CueOrigin();
            Vector2 cueSpot = _game.State.HasFlag(BilliardsFlags.BallInHand)
                ? from
                : Vector2.zero;

            // aimAt 而不是方向：`Shoot` 自己按快照算 `aimAt - from`，所以这里必须用同一个 from
            // 反推那个点，否则两边各算一次会差一个白球位。
            _game.Shoot(from + aimDirection, power, cueSpot);

            // 摆球位只对这一杆有效；下一次 ballInHand 要重新点。
            _placedCue = null;
        }

        #endregion

        #region Drawing

        private void OnGUI()
        {
            if (_game == null || _camera == null)
                return;

            _powerStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.white }
            };

            bool ballInHand = _game.State.HasFlag(BilliardsFlags.BallInHand);
            bool mine = CanAct();

            // 袋口禁放圈：画出来而不是让玩家自己撞上。白球中心落在离袋口中心 12.2 cm 以内会直接
            // 掉下去（袋口是台面上的真洞，#136），那一杆在打之前就没了。
            if (mine && ballInHand)
                DrawPocketKeepOuts();

            if (mine && ballInHand && _placedCue.HasValue)
                DrawCueGhost(_placedCue.Value);

            if (!_dragging || !mine)
                return;

            DrawAimAndPower();
        }

        private void DrawAimAndPower()
        {
            Vector2 drag = _currentTable - _anchorTable;
            float dragMetres = drag.magnitude;
            if (dragMetres < 1e-4f)
                return;

            Vector2 aimDirection = -drag.normalized;
            float power = PowerForDrag(dragMetres);
            bool over = IsOverRange(dragMetres);
            Vector2 from = CueOrigin();

            // 瞄准线：从白球往瞄准方向拉到库边，够玩家看清它指向哪一颗。
            Vector2 aimEnd = from + aimDirection * BilliardsTable.Length;
            DrawTableLine(from, aimEnd, over ? new Color(1f, 0.55f, 0.1f) : new Color(0.4f, 1f, 0.5f), 2f);

            // 球会滚到这 —— 力度控件的全部内容就是这个圈。超量程时它超出台面，那正是要看见的。
            float roll = RollDistanceForPower(power);
            DrawTableCircle(from + aimDirection * roll, BilliardsTable.BallRadius * 2.5f,
                over ? new Color(1f, 0.45f, 0.1f) : new Color(0.95f, 0.95f, 0.5f));

            // 拖动本身也画出来：手指与锚点之间那条线是「我拖了多长」的唯一凭据。
            DrawTableLine(_anchorTable, _currentTable, new Color(1f, 1f, 1f, 0.35f), 1.5f);
            DrawTableCircle(_anchorTable, BilliardsTable.BallRadius, new Color(1f, 1f, 1f, 0.5f));

            var label = new Vector2(_currentScreen.x + 14f, Screen.height - _currentScreen.y - 30f);
            string text = over
                ? $"力度 {power:F2}  ⚠ 超量程（比例已断）\n拖 {dragMetres:F2} m / 台面 {ProportionalDragMetres:F2} m"
                : $"力度 {power:F2}\n拖 {dragMetres:F2} m ⇒ 球滚 {roll:F2} m";

            var prev = _powerStyle.normal.textColor;
            _powerStyle.normal.textColor = over ? new Color(1f, 0.7f, 0.3f) : Color.white;
            GUI.Label(new Rect(label.x, label.y, 260f, 40f), text, _powerStyle);
            _powerStyle.normal.textColor = prev;
        }

        private void DrawPocketKeepOuts()
        {
            // 与 BilliardsTable.PushOutOfPockets 的 keepOut 同一个式子。两处重复是因为那个是
            // private；数字变了这里也要跟着变。
            float keepOut = BilliardsTable.PocketNotchHalf + BilliardsTable.BallRadius * 2f;
            var colour = new Color(1f, 0.35f, 0.35f, 0.5f);

            foreach (Vector3 pocket in BilliardsTable.Pockets)
                DrawTableCircle(new Vector2(pocket.x, pocket.z), keepOut, colour);
        }

        private void DrawCueGhost(Vector2 spot)
        {
            DrawTableCircle(spot, BilliardsTable.BallRadius, new Color(1f, 1f, 1f, 0.85f));
            DrawTableCircle(spot, BilliardsTable.BallRadius * 1.6f, new Color(1f, 1f, 1f, 0.35f));
        }

        private void DrawTableLine(Vector2 fromTable, Vector2 toTable, Color colour, float thickness)
        {
            DrawGuiLine(TableToGui(fromTable), TableToGui(toTable), colour, thickness);
        }

        private void DrawTableCircle(Vector2 centreTable, float radiusMetres, Color colour)
        {
            float metresPerPixel = MetresPerPixel();
            if (metresPerPixel <= 0f)
                return;

            float radiusPixels = radiusMetres / metresPerPixel;
            Vector2 centre = TableToGui(centreTable);

            const int segments = 20;
            Vector2 previous = default;
            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                var point = new Vector2(
                    centre.x + Mathf.Cos(angle) * radiusPixels,
                    centre.y + Mathf.Sin(angle) * radiusPixels);

                if (i > 0)
                    DrawGuiLine(previous, point, colour, 1.5f);

                previous = point;
            }
        }

        /// <summary>
        /// IMGUI 没有画线的原语，所以把一张白贴图旋转过去。矩阵改完必须还原 ——
        /// 它是全局的，留着会歪掉这一帧后面每一块 UI。
        /// </summary>
        private static void DrawGuiLine(Vector2 from, Vector2 to, Color colour, float thickness)
        {
            _lineTexture ??= Texture2D.whiteTexture;

            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.01f)
                return;

            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            Matrix4x4 matrix = GUI.matrix;
            Color previous = GUI.color;

            GUI.color = colour;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, length, thickness),
                _lineTexture);

            GUI.matrix = matrix;
            GUI.color = previous;
        }

        #endregion
    }
}
