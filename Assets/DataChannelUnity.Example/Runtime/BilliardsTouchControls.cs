using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 台球的操作层：手势进来，一条 <see cref="BilliardsGame.Shoot"/> 出去。
    ///
    /// 方案与实测数字在 `docs/billiards-touch-controls.md`。读数与横幅在 <see cref="BilliardsHud"/>，
    /// 两块分开是因为一块答「手指在做什么」、另一块答「局面是什么」。
    ///
    /// ## 三个控件，各管一件事
    ///
    /// 第一版把方向与力度合成一个手势（拖的方向给角度、拖的长度给力度）。**真机实测否掉了它**：
    /// 转方向与拉长度在同一根手指上互相干扰 —— 想微调角度就会改掉力度，反之亦然。所以拆开：
    ///
    /// | 控件 | 管什么 | 在哪 |
    /// |---|---|---|
    /// | 台面拖动 | **只**转方向 | 台面区（右侧条之外的全部） |
    /// | 右侧能量条 | **只**给力度，松手出杆 | 屏幕右侧固定竖条 |
    /// | 摆球 + 勾 | ballInHand 时定白球位 | 点台面选位，右侧出现勾 |
    ///
    /// ## 瞄准是相对的，且常显
    ///
    /// 瞄准线**一直画着**（能出杆时），不等手指按下 —— 玩家先看到球会往哪走，再决定要不要动它。
    /// 拖动改的是**角度增量**：手指横向位移 100 px 转 <see cref="DegreesPerHundredPixels"/> 度，
    /// 与手指的绝对位置无关。于是微调可以在屏幕任意舒适位置小幅滑动，不用把手伸到台面另一头，
    /// 也不会因为手指离白球远近不同而改变灵敏度。
    ///
    /// 一颗球在 4.7 寸横屏上只有 8.5 pt（Apple 的最小可点区域是 44 pt），所以**白球永远不被直接
    /// 触摸** —— 这一条从第一版起就没变，它是这套间接操作的全部理由。
    ///
    /// ## 力度：拖多远、球滚多远
    ///
    /// 距离随力度的**平方**变化（#137 实测 <c>d = 1.262·p²</c>），所以能量条的行程映射的是**距离**
    /// 而不是力度：条走到一半，球滚半个台面长，非线性藏进 <see cref="PowerForTravel"/> 里。
    /// 顶端 <see cref="OverRangeFraction"/> 那一段是开球用的超量程，比例在那里**可见地断掉**。
    ///
    /// ## 全部是本地意图
    ///
    /// 瞄准、摆球、拉能量条从不过网络（#127）。松手那一刻才有五个 float 走 Reliable 出去，
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
        [Tooltip("在 Awake 里把朝向锁成横屏。竖屏下半个台子在屏幕外，所以这是硬要求。")]
        private bool _lockLandscape = true;

        #endregion

        #region Constants

        /// <summary>
        /// #137 实测的自由滚系数：<c>d = 1.262·p²</c> 米。系数从 p = 0.5 起稳定到三位小数。
        /// </summary>
        public const float RollCoefficient = 1.262f;

        /// <summary>
        /// 手指横向移动 100 px 转多少度。
        ///
        /// 5° 是从这个游戏的精度需求推的：一次薄擦要亚度级精度，而 5°/100 px 意味着 1 px ≈ 0.05°，
        /// 于是一个 44 pt 的舒适滑动跨 2.2°，够粗调；亚度级微调则是几个像素的事，触屏分辨得出来。
        /// </summary>
        public const float DegreesPerHundredPixels = 5f;

        /// <summary>
        /// 能量条比例段的上限：条走到这个比例处，球正好滚一个台面长（power 1.50）。
        /// 再往上是超量程段。
        /// </summary>
        public const float OverRangeFraction = 0.7f;

        /// <summary>比例段末端对应的距离 —— 一个台面长。</summary>
        public const float ProportionalMaxMetres = BilliardsTable.Length;

        #endregion

        #region State

        /// <summary>
        /// 当前瞄准方向（台面平面上的单位向量）。**跨帧保持** —— 它是玩家调出来的状态，
        /// 不是某一次手势的产物。
        /// </summary>
        private Vector2 _aim = Vector2.left;

        /// <summary>这一回合有没有初始化过瞄准方向。换回合时重置一次到一个合理的默认朝向。</summary>
        private bool _aimInitialised;

        /// <summary>能量条当前行程，0–1。松手即出杆并归零。</summary>
        private float _travel;

        /// <summary>ballInHand 时点下的摆球位（已吸附到合法位），还没点过则为 null。</summary>
        private Vector2? _placedCue;

        /// <summary>摆球位有没有按过右侧那个勾确认。没确认之前不能出杆。</summary>
        private bool _cuePlacementConfirmed;

        private enum Drag { None, Aiming, Power }

        private Drag _drag;
        private Vector2 _lastScreen;

        private GUIStyle _label;
        private GUIStyle _hand;
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

        /// <summary>
        /// 换回合就把这一杆的意图全部丢掉：摆球位、确认、能量条、瞄准的初始化标记。
        /// 留着它们会让下一杆从上一杆的一半状态开始。
        /// </summary>
        private void OnStateChanged(BilliardsState state)
        {
            _travel = 0f;
            _drag = Drag.None;
            _aimInitialised = false;

            if (!state.HasFlag(BilliardsFlags.BallInHand))
            {
                _placedCue = null;
                _cuePlacementConfirmed = false;
            }
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
        /// 能量条行程（0–1）到力度。
        ///
        /// 下 <see cref="OverRangeFraction"/> 段：行程线性映射**距离** 0 到一个台面长，力度由
        /// <c>p = √(d/1.262)</c> 反解 —— 所以「条走到一半」＝「球滚半个台面长」，玩家想的是距离。
        /// 上段：线性升到 <see cref="BilliardsRules.MaxPower"/>，那是开球用的，比例在分界处断掉。
        /// </summary>
        public static float PowerForTravel(float travel)
        {
            travel = Mathf.Clamp01(travel);

            if (travel <= OverRangeFraction)
            {
                float metres = travel / OverRangeFraction * ProportionalMaxMetres;
                return Mathf.Sqrt(metres / RollCoefficient);
            }

            float atBreakpoint = Mathf.Sqrt(ProportionalMaxMetres / RollCoefficient);
            float t = (travel - OverRangeFraction) / (1f - OverRangeFraction);
            return Mathf.Lerp(atBreakpoint, BilliardsRules.MaxPower, t);
        }

        /// <summary>力度到自由滚距离（米）。画「球会停在这」那个圈用的。</summary>
        public static float RollDistanceForPower(float power) => RollCoefficient * power * power;

        /// <summary>行程进了超量程段没有 —— 那一段的比例是刻意断掉的。</summary>
        public static bool IsOverRange(float travel) => travel > OverRangeFraction;

        #endregion

        #region Layout

        /// <summary>
        /// 右侧那条能量条（GUI 坐标）。
        ///
        /// 上下各留出空间给右上的 <see cref="ConnectionDiagnosticsHud"/> 与右下的报告读数 ——
        /// 四块 UI 各占一角，这一条挤在右侧中段，不与它们重叠。
        /// </summary>
        public static Rect PowerBarRect()
        {
            const float width = 56f;
            const float rightMargin = 12f;
            const float topReserved = 78f;    // ConnectionDiagnosticsHud
            const float bottomReserved = 92f; // 报告读数

            float height = Mathf.Max(120f, Screen.height - topReserved - bottomReserved);
            return new Rect(Screen.width - width - rightMargin, topReserved, width, height);
        }

        /// <summary>ballInHand 时那个「确认摆球位」的勾。与能量条同一竖列，两者不同时出现。</summary>
        public static Rect ConfirmButtonRect()
        {
            Rect bar = PowerBarRect();
            const float size = 56f;
            return new Rect(bar.x, bar.y + (bar.height - size) * 0.5f, size, size);
        }

        /// <summary>台面手势区＝屏幕减掉右侧那条、减掉左上 RoomPanel、减掉左下 BilliardsHud。</summary>
        private static bool IsInTableArea(Vector2 guiPoint)
        {
            if (PowerBarRect().Contains(guiPoint))
                return false;

            // RoomPanel 与 BilliardsHud 都要让出去，否则点「断开」或者「再来一局」会顺手转
            // 一下瞄准。面板尺寸读 RoomPanel.ScreenRect，不再抄一份。
            if (RoomPanel.ScreenRect.Contains(guiPoint))
                return false;

            float hudHeight = 150f;
            if (new Rect(10f, Screen.height - hudHeight - 10f, 340f, hudHeight).Contains(guiPoint))
                return false;

            return true;
        }

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

        /// <summary>一屏幕像素等于多少台面米。画圈的半径要用它。</summary>
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

        #region Turn gate

        /// <summary>
        /// 现在能不能出杆。等重连那条是 #134 要的：对手在回来的路上，台子是他的。
        /// </summary>
        private bool IsMyShot()
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

        /// <summary>还欠一次摆球确认 —— 那之前不给出杆，也不给拉能量条。</summary>
        private bool AwaitingCuePlacement =>
            _game != null && _game.State.HasFlag(BilliardsFlags.BallInHand) && !_cuePlacementConfirmed;

        #endregion

        #region Input

        private void Update()
        {
            if (!IsMyShot())
            {
                // 冻住时把手上的意图丢掉，否则回合回来时会接着一条半途的拖动。
                _drag = Drag.None;
                _travel = 0f;
                return;
            }

            EnsureAimInitialised();

            if (TryReadPointer(out Vector2 screen, out bool pressed, out bool held, out bool released))
                DriveGesture(screen, pressed, held, released);
        }

        /// <summary>
        /// 换回合后第一次给一个合理的朝向：从白球指向脚点（球堆那一侧）。
        /// 开球时那正是要瞄的方向，其余情况下它是一个中性的起点，玩家再拖着调。
        /// </summary>
        private void EnsureAimInitialised()
        {
            if (_aimInitialised)
                return;

            Vector2 from = CueOrigin();
            var foot = new Vector2(BilliardsTable.FootSpot.x, BilliardsTable.FootSpot.z);
            Vector2 toFoot = foot - from;

            _aim = toFoot.sqrMagnitude > 1e-6f ? toFoot.normalized : Vector2.right;
            _aimInitialised = true;
        }

        /// <summary>
        /// 一个指头或者鼠标左键，取先有的那个。工程走的是旧 Input（`activeInputHandler: 0`），
        /// 所以两条都在 <c>Input</c> 上。
        /// </summary>
        private static bool TryReadPointer(out Vector2 screen, out bool pressed, out bool held,
            out bool released)
        {
            screen = default;
            pressed = false;
            held = false;
            released = false;

            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                screen = touch.position;
                pressed = touch.phase == TouchPhase.Began;
                released = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                held = !pressed && !released;
                return true;
            }

            screen = Input.mousePosition;
            pressed = Input.GetMouseButtonDown(0);
            released = Input.GetMouseButtonUp(0);
            held = Input.GetMouseButton(0) && !pressed;
            return pressed || held || released;
        }

        private void DriveGesture(Vector2 screen, bool pressed, bool held, bool released)
        {
            var gui = new Vector2(screen.x, Screen.height - screen.y);

            if (pressed)
            {
                _lastScreen = screen;
                // 按下那一刻就锁定这根手指管哪个控件，之后滑出去也不换 —— 否则从台面滑进能量条
                // 会在中途变成出杆，而那正是第一版方向与力度互相干扰的那类问题。
                _drag = ChooseControl(gui);

                if (_drag == Drag.Power)
                    _travel = TravelAt(gui);

                return;
            }

            if (_drag == Drag.None)
                return;

            if (held)
            {
                if (_drag == Drag.Aiming)
                {
                    // 相对：只看这一帧的横向位移，与手指在哪无关。
                    float deltaX = screen.x - _lastScreen.x;
                    RotateAim(deltaX / 100f * DegreesPerHundredPixels);
                }
                else
                {
                    _travel = TravelAt(gui);
                }

                _lastScreen = screen;
                return;
            }

            if (!released)
                return;

            // 松手：能量条那根出杆，台面那根只是结束一次转向。
            if (_drag == Drag.Power)
                ShootWithCurrentAim();

            _drag = Drag.None;
        }

        /// <summary>按下的位置决定这根手指管哪个控件。</summary>
        private Drag ChooseControl(Vector2 gui)
        {
            if (AwaitingCuePlacement)
            {
                // 摆球阶段：勾与台面点选各走各的，都不是拖动。
                if (ConfirmButtonRect().Contains(gui))
                {
                    if (_placedCue.HasValue)
                        _cuePlacementConfirmed = true;

                    return Drag.None;
                }

                if (IsInTableArea(gui) && TryScreenToTable(GuiToScreen(gui), out Vector2 spot))
                    _placedCue = SnapCueSpot(spot);

                return Drag.None;
            }

            if (PowerBarRect().Contains(gui))
                return Drag.Power;

            return IsInTableArea(gui) ? Drag.Aiming : Drag.None;
        }

        private static Vector2 GuiToScreen(Vector2 gui) => new Vector2(gui.x, Screen.height - gui.y);

        private void RotateAim(float degrees)
        {
            if (Mathf.Abs(degrees) < 1e-5f)
                return;

            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            _aim = new Vector2(_aim.x * cos - _aim.y * sin, _aim.x * sin + _aim.y * cos).normalized;
        }

        /// <summary>能量条上一个 GUI 点对应的行程。条底是 0，条顶是 1。</summary>
        private static float TravelAt(Vector2 gui)
        {
            Rect bar = PowerBarRect();
            return Mathf.Clamp01((bar.yMax - gui.y) / bar.height);
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

        /// <summary>松手：把当前的方向与力度换成五个 float 交给 <see cref="BilliardsGame.Shoot"/>。</summary>
        private void ShootWithCurrentAim()
        {
            float power = PowerForTravel(_travel);
            _travel = 0f;

            // 条基本没拉动就不是一杆 —— 那是一次误触。
            if (power < 0.05f)
                return;

            if (AwaitingCuePlacement)
                return;

            Vector2 from = CueOrigin();
            Vector2 cueSpot = _game.State.HasFlag(BilliardsFlags.BallInHand) ? from : Vector2.zero;

            // aimAt 而不是方向：`Shoot` 自己按快照算 `aimAt - from`，所以这里必须用同一个 from
            // 反推那个点，否则两边各算一次会差一个白球位。
            _game.Shoot(from + _aim, power, cueSpot);

            _placedCue = null;
            _cuePlacementConfirmed = false;
        }

        #endregion

        #region Drawing

        private void OnGUI()
        {
            if (_game == null || _camera == null)
                return;

            _label ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white }
            };

            _hand ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                alignment = TextAnchor.MiddleCenter
            };

            if (!IsMyShot())
                return;

            if (AwaitingCuePlacement)
            {
                DrawCuePlacement();
                return;
            }

            DrawAim();
            DrawPowerBar();
        }

        /// <summary>
        /// 摆球阶段：袋口禁放圈、白球影子、跟着手指的手、右侧那个勾。
        /// </summary>
        private void DrawCuePlacement()
        {
            // 袋口禁放圈：画出来而不是让玩家自己撞上。白球中心落在离袋口中心 12.2 cm 以内会直接
            // 掉下去（袋口是台面上的真洞，#136），那一杆在打之前就没了。
            float keepOut = BilliardsTable.PocketNotchHalf + BilliardsTable.BallRadius * 2f;
            foreach (Vector3 pocket in BilliardsTable.Pockets)
                DrawTableCircle(new Vector2(pocket.x, pocket.z), keepOut, new Color(1f, 0.35f, 0.35f, 0.5f));

            if (_placedCue.HasValue)
            {
                DrawTableCircle(_placedCue.Value, BilliardsTable.BallRadius, new Color(1f, 1f, 1f, 0.9f));
                DrawTableCircle(_placedCue.Value, BilliardsTable.BallRadius * 1.8f, new Color(1f, 1f, 1f, 0.4f));
            }

            // 一只手拿着球，跟着手指 —— 「现在是你在放球」这件事靠它说，不靠一行字。
            if (Input.touchCount > 0 || Input.mousePresent)
            {
                Vector2 screen = Input.touchCount > 0 ? Input.GetTouch(0).position : (Vector2)Input.mousePosition;
                var gui = new Vector2(screen.x, Screen.height - screen.y);
                if (IsInTableArea(gui))
                    GUI.Label(new Rect(gui.x - 20f, gui.y - 44f, 40f, 40f), "✋", _hand);
            }

            GUI.Label(new Rect(PowerBarRect().x - 210f, ConfirmButtonRect().y - 26f, 200f, 24f),
                _placedCue.HasValue ? "点右边的勾确认" : "点台面选白球位置", _label);

            // 勾：没选位置之前是灰的 —— 一个能按但什么都不做的按钮比一个灰的更难懂。
            Rect confirm = ConfirmButtonRect();
            GUI.enabled = _placedCue.HasValue;
            if (GUI.Button(confirm, "✔"))
            {
                if (_placedCue.HasValue)
                    _cuePlacementConfirmed = true;
            }
            GUI.enabled = true;
        }

        /// <summary>
        /// 瞄准线常显：从白球沿当前方向拉到库边，末端一个箭头。力度 > 0 时再画一个「球会停在这」的圈。
        /// </summary>
        private void DrawAim()
        {
            Vector2 from = CueOrigin();
            float power = PowerForTravel(_travel);
            bool over = IsOverRange(_travel);
            Color colour = over ? new Color(1f, 0.55f, 0.1f) : new Color(0.4f, 1f, 0.5f);

            Vector2 end = from + _aim * BilliardsTable.Length;
            DrawTableLine(from, end, colour, 2f);

            // 白球自己也描一圈：它只有 8.5 px，描出来才看得出线是从它出发的。
            DrawTableCircle(from, BilliardsTable.BallRadius * 1.4f, new Color(1f, 1f, 1f, 0.6f));

            // 箭头：两条短线，指出方向。没有它这条线两头长得一样。
            Vector2 tipGui = TableToGui(end);
            Vector2 dirGui = (tipGui - TableToGui(from)).normalized;
            var perp = new Vector2(-dirGui.y, dirGui.x);
            DrawGuiLine(tipGui, tipGui - dirGui * 14f + perp * 7f, colour, 2f);
            DrawGuiLine(tipGui, tipGui - dirGui * 14f - perp * 7f, colour, 2f);

            if (_travel <= 0.001f)
                return;

            float roll = RollDistanceForPower(power);
            DrawTableCircle(from + _aim * roll, BilliardsTable.BallRadius * 2.5f,
                over ? new Color(1f, 0.45f, 0.1f) : new Color(0.95f, 0.95f, 0.5f));
        }

        /// <summary>
        /// 右侧那条能量条。分界线画出来，因为比例在那里**断掉** —— 不画的话超量程段看着只是
        /// 「更大力一点」，而它其实是另一套映射。
        /// </summary>
        private void DrawPowerBar()
        {
            Rect bar = PowerBarRect();
            float power = PowerForTravel(_travel);
            bool over = IsOverRange(_travel);

            Color prev = GUI.color;

            // 槽
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);

            // 已填充部分，从底往上
            float filled = bar.height * Mathf.Clamp01(_travel);
            GUI.color = over ? new Color(1f, 0.5f, 0.1f, 0.85f) : new Color(0.35f, 0.85f, 0.45f, 0.85f);
            GUI.DrawTexture(new Rect(bar.x, bar.yMax - filled, bar.width, filled), Texture2D.whiteTexture);

            // 比例段与超量程段的分界
            float splitY = bar.yMax - bar.height * OverRangeFraction;
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            GUI.DrawTexture(new Rect(bar.x, splitY - 1f, bar.width, 2f), Texture2D.whiteTexture);
            GUI.color = prev;

            GUI.Label(new Rect(bar.x - 74f, splitY - 10f, 70f, 20f), "↑超量程", _label);

            // 读数：力度与它对应的距离，那是玩家真正在选的量。
            string text = over
                ? $"力度 {power:F2}\n⚠ 超量程"
                : $"力度 {power:F2}\n球滚 {RollDistanceForPower(power):F2} m";
            GUI.Label(new Rect(bar.x - 150f, bar.y - 2f, 145f, 40f), text, _label);

            GUI.Label(new Rect(bar.x - 150f, bar.yMax - 22f, 145f, 20f), "拖这条，松手出杆", _label);
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
