using DataChannelUnity.Example;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DataChannelUnity.EditorTools
{
    /// <summary>
    /// Builds the billiards scene from code rather than by hand, so the layout is reproducible
    /// and reviewable in a diff. #136's rack is fixed by specification; a hand-placed scene
    /// would make that claim unverifiable.
    /// </summary>
    public static class BilliardsSceneBuilder
    {
        private const string ScenePath =
            "Assets/DataChannelUnity.Example/Scenes/Billiards over DataChannel.unity";

        private static PhysicMaterial _railMaterial;
        private static PhysicMaterial _ballMaterial;
        private static PhysicMaterial _surfaceMaterial;

        [MenuItem("Tools/DataChannel Example/Build Billiards Scene")]
        public static void Build()
        {
            // Reset: a second run must not reuse references from the previous one.
            _railMaterial = null;
            _ballMaterial = null;
            _surfaceMaterial = null;

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateCamera();
            CreateLight();
            CreateTable();
            CreateNetworking();
            BilliardsRack rack = CreateRack();
            CreateBalls(rack);

            // Saved *before* the SceneIds are assigned, and this order is load-bearing. A scene created
            // by NewScene has no name until it is written to disk, and NetworkObject's OnValidate zeroes
            // SceneId outright while that is true (NetworkObject.Serialized.cs:167-171) — it reads an
            // unnamed scene as "not a scene object". Assigning ids first therefore produced a scene with
            // sixteen zeroes and a log line claiming 16/16, since the write did happen and was then
            // undone. Save, assign, save again.
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved)
            {
                Debug.LogError("[Billiards] Scene save FAILED; SceneIds not assigned.");
                return;
            }

            AssignSceneIds();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogError("[Billiards] Second save FAILED; SceneIds are not on disk and the " +
                               "balls will not spawn.");
                return;
            }

            Debug.Log($"[Billiards] Scene written to {ScenePath}");
            RegisterInBuildSettings();
            VerifySceneIdsOnDisk();
        }

        /// <summary>
        /// Reads the SceneIds back after the final save. Present because the failure this guards against
        /// was silent in both directions: the ids were written, OnValidate undid them, and the assigning
        /// code still logged success. An in-memory check would have reported the same false pass.
        /// </summary>
        private static void VerifySceneIdsOnDisk()
        {
            AssetDatabase.Refresh();

            int zero = 0;
            var seen = new System.Collections.Generic.HashSet<ulong>();
            foreach (FishNet.Object.NetworkObject nob in
                     Object.FindObjectsOfType<FishNet.Object.NetworkObject>(true))
            {
                var so = new SerializedObject(nob);
                SerializedProperty p = so.FindProperty("SceneId");
                ulong id = p == null ? 0UL : unchecked((ulong)p.longValue);

                if (id == 0UL)
                {
                    zero++;
                    Debug.LogError($"[Billiards] {nob.name} has SceneId 0 and will not spawn.");
                }
                else if (!seen.Add(id))
                {
                    Debug.LogError($"[Billiards] {nob.name} has duplicate SceneId {id}.");
                }
            }

            Debug.Log(zero == 0
                ? $"[Billiards] Verified {seen.Count} distinct non-zero SceneIds."
                : $"[Billiards] {zero} NetworkObjects still have no SceneId.");
        }

        /// <summary>
        /// Adds the scene to build settings if it is not already there. Needed because the PlayMode
        /// measurement loads it through the runtime SceneManager, which can only see scenes listed
        /// here — an unlisted scene fails as "scene not loaded", which reads like a broken test rather
        /// than a missing entry.
        /// </summary>
        private static void RegisterInBuildSettings()
        {
            EditorBuildSettingsScene[] existing = EditorBuildSettings.scenes;
            foreach (EditorBuildSettingsScene entry in existing)
            {
                if (entry.path == ScenePath)
                    return;
            }

            var updated = new EditorBuildSettingsScene[existing.Length + 1];
            System.Array.Copy(existing, updated, existing.Length);
            updated[existing.Length] = new EditorBuildSettingsScene(ScenePath, true);
            EditorBuildSettings.scenes = updated;

            Debug.Log($"[Billiards] Added to build settings: {ScenePath}");
        }

        /// <summary>
        /// NetworkManager plus the transport under test. Every other FishNet manager is created by
        /// NetworkManager itself in Awake (GetOrCreateComponent, NetworkManager.cs:320-333), so
        /// adding them here would only risk disagreeing with its defaults.
        /// </summary>
        private static void CreateNetworking()
        {
            var go = new GameObject("NetworkManager");
            var manager = go.AddComponent<FishNet.Managing.NetworkManager>();
            go.AddComponent<DataChannelTransport>();

            // Measurement lives on the same object so it can find the transport without wiring.
            go.AddComponent<OutboundByteMeter>();

            var timeManager = go.AddComponent<FishNet.Managing.Timing.TimeManager>();
            var so = new SerializedObject(timeManager);

            // PhysicsMode.TimeManager (1): FishNet steps physics inside its own tick. Without this
            // the balls would move on Unity's fixed timestep while transforms replicate on the
            // network tick, and the two would drift apart. It also makes the physics step equal to
            // the tick — 33 ms at TickRate 30 — which is why the balls need continuous collision
            // detection (see BilliardsBall).
            SetEnum(so, "_physicsMode", 1);

            // 30/s per #113: rolling balls at the demo's 10/s look bad however well interpolated.
            SetInt(so, "_tickRate", 30);
            so.ApplyModifiedPropertiesWithoutUndo();

            // SpawnablePrefabs has to be assigned here, not left to FishNet's auto-fill. That fill is
            // gated on gameObject.scene.name being non-empty (NetworkManager.cs:492) — the same
            // unnamed-scene condition that zeroes SceneIds — so during a build it is skipped and never
            // reaches the saved scene. In the Editor it then gets filled on load, which makes the scene
            // look fine while a runtime load fails with "SpawnablePrefabs is null".
            const string prefabsPath = "Assets/DefaultPrefabObjects.asset";
            var prefabs =
                AssetDatabase.LoadAssetAtPath<FishNet.Managing.Object.PrefabObjects>(prefabsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[Billiards] {prefabsPath} not found; NetworkManager will fail to " +
                               "start outside the Editor.");
            }
            else
            {
                var managerSo = new SerializedObject(manager);
                SerializedProperty prefabsProp = Find(managerSo, "_spawnablePrefabs");
                if (prefabsProp != null)
                {
                    prefabsProp.objectReferenceValue = prefabs;
                    managerSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // No transport is assigned here on purpose: TransportManager has no serialized transport
            // field — it takes whatever Transport sits on its own GameObject (TransportManager.cs:803),
            // which is the DataChannelTransport added above.
            //
            // Worth knowing how this fails: if that component is ever missing, FishNet does not
            // complain, it silently adds Tugboat instead (TransportManager.cs:268). The measurement
            // would then run over UDP sockets and still look plausible, so the meter checks the
            // transport's type at runtime rather than trusting this wiring.
            Debug.Log($"[Billiards] NetworkManager built (TickRate 30, PhysicsMode.TimeManager) " +
                      $"on {manager.name}.");
        }

        private static void CreateCamera()
        {
            var go = new GameObject("Top-Down Camera");
            Camera cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            // Half-height must cover the table's short axis plus the parked-ball row.
            cam.orthographicSize = BilliardsTable.HalfWidth + 0.55f;
            cam.backgroundColor = new Color(0.06f, 0.18f, 0.10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            go.transform.position = new Vector3(0f, 5f, 0f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.tag = "MainCamera";
        }

        private static void CreateLight()
        {
            var go = new GameObject("Directional Light");
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private const string MaterialFolder = "Assets/DataChannelUnity.Example/Physics";

        /// <summary>
        /// Cushion. Restitution around 0.6 is roughly what a real cushion returns; friction is low
        /// because a ball glancing off a rail should keep most of its speed along the rail.
        /// </summary>
        private static PhysicMaterial CreateRailMaterial()
        {
            return SaveMaterial(new PhysicMaterial
            {
                bounciness = 0.6f,
                dynamicFriction = 0.15f,
                staticFriction = 0.15f,
                bounceCombine = PhysicMaterialCombine.Maximum,
                frictionCombine = PhysicMaterialCombine.Multiply
            }, "BilliardsRail");
        }

        /// <summary>
        /// Ball on ball is nearly elastic (real balls return ~0.95), which is what makes a rack
        /// scatter instead of absorbing the cue ball and moving off as one lump.
        /// </summary>
        private static PhysicMaterial CreateBallMaterial()
        {
            return SaveMaterial(new PhysicMaterial
            {
                bounciness = 0.95f,
                dynamicFriction = 0.2f,
                staticFriction = 0.2f,
                bounceCombine = PhysicMaterialCombine.Maximum,
                frictionCombine = PhysicMaterialCombine.Multiply
            }, "BilliardsBall");
        }

        /// <summary>
        /// The cloth. Almost no bounce — a ball landing on the slate should settle, not hop — and
        /// high friction, because with gravity on this is what actually slows a ball down. PhysX
        /// models no rolling resistance, so friction here plus angularDrag on the body are between
        /// them standing in for it.
        /// </summary>
        private static PhysicMaterial CreateSurfaceMaterial()
        {
            return SaveMaterial(new PhysicMaterial
            {
                bounciness = 0.02f,
                dynamicFriction = 0.6f,
                staticFriction = 0.7f,
                bounceCombine = PhysicMaterialCombine.Minimum,
                frictionCombine = PhysicMaterialCombine.Average
            }, "BilliardsSurface");
        }

        /// <summary>
        /// Physics materials must live as assets: one created in memory is lost when the scene
        /// reloads, and the colliders silently fall back to the default (bounciness 0) — which
        /// looks exactly like a table whose cushions do not work.
        /// </summary>
        private static PhysicMaterial SaveMaterial(PhysicMaterial material, string name)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/DataChannelUnity.Example", "Physics");

            string path = $"{MaterialFolder}/{name}.physicMaterial";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(material, path);
            return AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
        }

        /// <summary>
        /// The playing surface, assembled from the boxes <see cref="BilliardsTable.SurfacePieces"/>
        /// lays out so the pocket notches stay empty. Reading the layout from the runtime geometry
        /// rather than recomputing it here is what keeps the holes and the pockets in the same
        /// places — the failure mode of computing it twice is a table whose pockets are decoration.
        /// </summary>
        private static void BuildSurface(Transform parent)
        {
            var root = new GameObject("Surface");
            root.transform.SetParent(parent, false);

            const float slateThickness = 0.02f;
            // Top face sits one ball radius below the ball centre line, so a resting ball's centre
            // lands on BilliardsTable.BallY.
            float centreY = BilliardsTable.BallY - BilliardsTable.BallRadius - slateThickness * 0.5f;

            BilliardsTable.SurfacePiece[] pieces = BilliardsTable.SurfacePieces();
            for (int i = 0; i < pieces.Length; i++)
            {
                BilliardsTable.SurfacePiece piece = pieces[i];
                GameObject slate = GameObject.CreatePrimitive(PrimitiveType.Cube);
                slate.name = $"Slate {i}";
                slate.transform.SetParent(root.transform, false);
                slate.transform.localPosition =
                    new Vector3(piece.Centre.x, centreY, piece.Centre.y);
                slate.transform.localScale =
                    new Vector3(piece.Size.x, slateThickness, piece.Size.y);
                slate.GetComponent<Collider>().sharedMaterial =
                    _surfaceMaterial ??= CreateSurfaceMaterial();
                Paint(slate, new Color(0.10f, 0.42f, 0.24f));
            }

            Debug.Log($"[Billiards] Surface built from {pieces.Length} pieces.");
        }

        private static GameObject CreateTable()
        {
            var root = new GameObject("Table");

            BuildSurface(root.transform);

            // Four unbroken walls. They no longer need gaps: the pockets are holes in the surface,
            // so a ball leaves downward rather than sideways.
            float railX = BilliardsTable.HalfLength + BilliardsTable.RailThickness * 0.5f;
            float railZ = BilliardsTable.HalfWidth + BilliardsTable.RailThickness * 0.5f;
            float longSpan = BilliardsTable.Length + BilliardsTable.RailThickness * 2f;
            float shortSpan = BilliardsTable.Width + BilliardsTable.RailThickness * 2f;

            CreateRail(root.transform, "Rail -X", new Vector3(-railX, 0f, 0f),
                new Vector3(BilliardsTable.RailThickness, BilliardsTable.RailHeight, shortSpan));
            CreateRail(root.transform, "Rail +X", new Vector3(railX, 0f, 0f),
                new Vector3(BilliardsTable.RailThickness, BilliardsTable.RailHeight, shortSpan));
            CreateRail(root.transform, "Rail -Z", new Vector3(0f, 0f, -railZ),
                new Vector3(longSpan, BilliardsTable.RailHeight, BilliardsTable.RailThickness));
            CreateRail(root.transform, "Rail +Z", new Vector3(0f, 0f, railZ),
                new Vector3(longSpan, BilliardsTable.RailHeight, BilliardsTable.RailThickness));

            CreatePocketMarkers(root.transform);
            return root;
        }

        /// <summary>
        /// Rails are thick and tall on purpose. Physics runs on the FishNet tick, so a step is
        /// 33 ms at TickRate 30 and a fast ball covers ~10 cm in one — a thin rail is a rail a
        /// ball tunnels through.
        /// </summary>
        private static void CreateRail(Transform parent, string name, Vector3 position, Vector3 scale)
        {
            GameObject rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rail.name = name;
            rail.transform.SetParent(parent, false);
            rail.transform.localPosition = position;
            rail.transform.localScale = scale;
            rail.GetComponent<Collider>().sharedMaterial = _railMaterial ??= CreateRailMaterial();
            Paint(rail, new Color(0.28f, 0.16f, 0.09f));
        }

        private static void CreatePocketMarkers(Transform parent)
        {
            var root = new GameObject("Pockets");
            root.transform.SetParent(parent, false);

            Vector3[] centres = BilliardsTable.Pockets;
            for (int i = 0; i < centres.Length; i++)
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = $"Pocket {i}";
                marker.transform.SetParent(root.transform, false);
                // Below the surface, so it reads as a hole with depth rather than a disc painted on
                // the cloth. Nothing rests on it — a ball that gets this far keeps falling.
                marker.transform.localPosition = centres[i] + new Vector3(0f, -0.10f, 0f);
                float mouth = BilliardsTable.PocketNotchHalf * 2f;
                marker.transform.localScale = new Vector3(mouth, 0.01f, mouth);
                // Visual only: the surface pieces already leave this space empty, and capture is a
                // height test (BilliardsRack.IsInPocket). A collider here would floor the pocket.
                Object.DestroyImmediate(marker.GetComponent<Collider>());
                Paint(marker, new Color(0.03f, 0.03f, 0.03f));
            }
        }

        private static BilliardsRack CreateRack()
        {
            var go = new GameObject("Billiards");
            BilliardsRack rack = go.AddComponent<BilliardsRack>();
            // Verification scaffolding for #136, built in so a rebuild does not silently drop it.
            go.AddComponent<BilliardsBreakProbe>();
            return rack;
        }

        /// <summary>
        /// The 16 balls, each its own root NetworkObject.
        ///
        /// Kept at the scene root rather than under the table: NetworkTransform replicates
        /// *localPosition*, while the ball physics and the table geometry work in world space. Those
        /// coincide only while every ancestor sits at identity — so parenting the balls to the table
        /// would make replication silently wrong the day somebody nudges the table, with physics
        /// still perfectly correct. Cheaper to remove the dependency than to document it.
        /// </summary>
        private static void CreateBalls(BilliardsRack rack)
        {
            var root = new GameObject("Balls");

            for (int number = 0; number <= 15; number++)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = number == 0 ? "Ball Cue" : $"Ball {number:00}";
                go.transform.SetParent(root.transform, false);
                go.transform.localScale = Vector3.one * (BilliardsTable.BallRadius * 2f);
                go.transform.localPosition = number == 0
                    ? BilliardsTable.HeadSpot
                    : BilliardsTable.RackPosition(number);

                // Mirror what BilliardsBall.ConfigureBody applies at runtime. Without this the
                // serialised scene holds Unity's defaults (discrete CCD, no drag) and anyone
                // inspecting it sees a configuration the game does not actually run with.
                var body = go.AddComponent<Rigidbody>();
                body.useGravity = true;
                body.constraints = RigidbodyConstraints.None;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.drag = 0.12f;
                body.angularDrag = 1.4f;
                body.sleepThreshold = 0.0001f;

                go.GetComponent<Collider>().sharedMaterial = _ballMaterial ??= CreateBallMaterial();
                BilliardsBall ball = go.AddComponent<BilliardsBall>();
                SetPrivateInt(ball, "_number", number);
                Paint(go, ColourFor(number));

                ConfigureReplication(go);
            }

            Debug.Log($"[Billiards] 16 balls built under {root.name} with NetworkTransform.");
        }

        /// <summary>
        /// One ball's replication shape, per #131: position only, both axes unpacked, no rotation and
        /// no scale, server authoritative, every tick.
        /// </summary>
        private static void ConfigureReplication(GameObject go)
        {
            go.AddComponent<FishNet.Object.NetworkObject>();

            var nt = go.AddComponent<FishNet.Component.Transforming.NetworkTransform>();
            var so = new SerializedObject(nt);

            SetBool(so, "_synchronizePosition", true);
            SetBool(so, "_synchronizeRotation", false);
            SetBool(so, "_synchronizeScale", false);

            // Server authoritative. This also decides what a *client's* rigidbody does: with
            // _clientAuthoritative false, CanMakeKinematic returns !isServerStarted
            // (NetworkTransform.cs:901), so together with _componentConfiguration below a pure
            // client's body goes kinematic instead of simulating its own physics underneath the
            // positions being replicated to it. #131 settled the wire format but not this, and it is
            // invisible in a host-only measurement — the host *is* the server, so its bodies stay
            // dynamic either way.
            SetBool(so, "_clientAuthoritative", false);
            SetEnum(so, "_componentConfiguration", 2); // Rigidbody

            // Every tick. The burst under measurement is the whole point; sending less often would
            // measure the throttle rather than the load.
            SetInt(so, "_interval", 1);

            // Unpacked position. Packed is ×100 into a short — 1 cm quantisation, which is a fifth of
            // a ball diameter and visible as jitter, while spending its ±327 m range on a 2.84 m
            // table (#131).
            SetEnumByName(so, "_packing.Position", "Unpacked");

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Color ColourFor(int number)
        {
            if (number == 0)
                return Color.white;
            if (number == 8)
                return new Color(0.05f, 0.05f, 0.05f);

            // Solids 1-7 saturated, stripes 9-15 the same hues washed out. Group membership is
            // pre-assigned by number (#132), so colour is a reading aid, not state.
            Color[] hues =
            {
                new Color(0.95f, 0.80f, 0.10f),
                new Color(0.10f, 0.30f, 0.85f),
                new Color(0.85f, 0.15f, 0.12f),
                new Color(0.45f, 0.15f, 0.60f),
                new Color(0.95f, 0.45f, 0.10f),
                new Color(0.10f, 0.55f, 0.25f),
                new Color(0.55f, 0.15f, 0.15f)
            };

            int index = (number < 8 ? number : number - 8) - 1;
            Color hue = hues[Mathf.Clamp(index, 0, hues.Length - 1)];
            return number < 8 ? hue : Color.Lerp(hue, Color.white, 0.55f);
        }

        private static void Paint(GameObject go, Color colour)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return;

            // URP project: the built-in Standard shader renders magenta here. It is not caught by
            // Shader.isSupported either — that returns true for Standard under URP, so only a
            // screenshot reveals it. Hence an explicit URP shader lookup with a loud failure.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[Billiards] URP/Lit shader not found; materials would render magenta.");
                return;
            }

            var material = new Material(shader);
            material.SetColor("_BaseColor", colour);
            renderer.sharedMaterial = material;
        }

        /// <summary>
        /// Finds a serialized property, or logs and returns null. Every setter below goes through
        /// this: these are private fields of a third-party package, so a rename upstream would
        /// otherwise leave the value at its default and the scene would look configured when it is
        /// not.
        /// </summary>
        private static SerializedProperty Find(SerializedObject so, string path)
        {
            SerializedProperty p = so.FindProperty(path);
            if (p == null)
            {
                Debug.LogError($"[Billiards] Serialized field '{path}' not found on " +
                               $"{so.targetObject.GetType().Name} — value left at its default.");
            }

            return p;
        }

        private static void SetBool(SerializedObject so, string path, bool value)
        {
            SerializedProperty p = Find(so, path);
            if (p != null)
                p.boolValue = value;
        }

        private static void SetInt(SerializedObject so, string path, int value)
        {
            SerializedProperty p = Find(so, path);
            if (p != null)
                p.intValue = value;
        }

        private static void SetEnum(SerializedObject so, string path, int value)
        {
            SerializedProperty p = Find(so, path);
            if (p != null)
                p.enumValueIndex = value;
        }

        /// <summary>
        /// Sets an enum by its name rather than its ordinal. Worth the extra work for values that
        /// come from a third-party package: an ordinal silently means something else if the enum
        /// gains a member, and "position packing quietly became Packed" is not a failure anyone
        /// would notice from a byte count alone.
        /// </summary>
        private static void SetEnumByName(SerializedObject so, string path, string name)
        {
            SerializedProperty p = Find(so, path);
            if (p == null)
                return;

            int index = System.Array.IndexOf(p.enumNames, name);
            if (index < 0)
            {
                Debug.LogError($"[Billiards] Enum '{name}' not found for '{path}'; " +
                               $"available: {string.Join(", ", p.enumNames)}");
                return;
            }

            p.enumValueIndex = index;
        }

        private static void SetPrivateInt(Object target, string field, int value)
        {
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogError($"[Billiards] Field {field} not found on {target.GetType().Name}");
                return;
            }

            p.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Gives every NetworkObject in the scene a SceneId and marks it as having been active during
        /// edit. Both are required before FishNet will spawn a scene object, and both fail *quietly*
        /// — which is why this is done explicitly rather than left to Unity's callbacks.
        ///
        /// <para>SceneId: <c>IsSceneObject</c> is just <c>SceneId != 0</c> (NetworkObject.cs:82), and
        /// SetupSceneObjects skips anything where that is false (ServerObjects.cs:471) without a
        /// message. NetworkObject does assign ids from OnValidate, but that path rebuilds the whole
        /// scene at once and is rate-limited to one rebuild per 250 ms
        /// (NetworkObject.Serialized.cs:193). Adding sixteen NetworkObjects in a loop lands entirely
        /// inside one such window, so the first ball would get an id and the other fifteen would
        /// not.</para>
        ///
        /// <para>WasActiveDuringEdit_Set1: without it FishNet logs "needs to be reserialized" and
        /// skips the object (ServerObjects.cs:475).</para>
        ///
        /// The ids are assigned in hierarchy-path order so a rebuild reproduces the same scene rather
        /// than churning the diff.
        /// </summary>
        private static void AssignSceneIds()
        {
            var objects = new System.Collections.Generic.List<FishNet.Object.NetworkObject>(
                Object.FindObjectsOfType<FishNet.Object.NetworkObject>(true));

            objects.Sort((a, b) => string.CompareOrdinal(HierarchyPath(a.transform),
                HierarchyPath(b.transform)));

            // Scoped to this scene's path so ids cannot collide with another scene's when both are
            // loaded — the same reason FishNet mixes in a scene path hash.
            uint sceneHash = StableHash(ScenePath);
            var seen = new System.Collections.Generic.HashSet<ulong>();
            int assigned = 0;

            for (int i = 0; i < objects.Count; i++)
            {
                FishNet.Object.NetworkObject nob = objects[i];
                ulong sceneId = ((ulong)sceneHash << 16) | (uint)(i + 1);

                if (!seen.Add(sceneId))
                {
                    Debug.LogError($"[Billiards] Duplicate SceneId {sceneId} for {nob.name}.");
                    continue;
                }

                var so = new SerializedObject(nob);
                SerializedProperty idProp = Find(so, "SceneId");
                SerializedProperty wasActive = Find(so, "WasActiveDuringEdit");
                SerializedProperty wasActiveSet = Find(so, "WasActiveDuringEdit_Set1");

                if (idProp == null || wasActive == null || wasActiveSet == null)
                    continue;

                // longValue rather than ulongValue: the latter does not exist on SerializedProperty in
                // 2022.3. The ids stay well inside long's positive range, so the reinterpretation is
                // exact.
                idProp.longValue = unchecked((long)sceneId);
                wasActive.boolValue = true;
                wasActiveSet.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                assigned++;
            }

            Debug.Log($"[Billiards] SceneIds assigned to {assigned}/{objects.Count} NetworkObjects.");

            if (assigned != objects.Count)
            {
                Debug.LogError("[Billiards] Some NetworkObjects have no SceneId; those will not " +
                               "spawn, and FishNet will not say so.");
            }
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent)
                path = $"{p.name}/{path}";
            return path;
        }

        /// <summary>FNV-1a. Any stable hash does; this one avoids depending on a package internal.</summary>
        private static uint StableHash(string value)
        {
            uint hash = 2166136261u;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
