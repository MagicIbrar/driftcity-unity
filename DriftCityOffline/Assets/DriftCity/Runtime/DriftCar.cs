using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity
{
    /// <summary>
    /// The original client's car handling (SkidRush "XiDynamicControl" update, DriftCity.exe 0x4809c0), recovered in
    /// DriftCity_RE/PHYSICS.md. The car is a point mass stepped every 3 ms: engine, brake and drag forces act along the
    /// direction of travel, steering turns the velocity with a lateral acceleration v^2/R from a steady-state radius
    /// R = (L + K v^2/g) / steer, and the heading is the travel direction plus a slip angle that springs toward a
    /// target (NOSLIP grip, FRSLIP while the drift button is held, MTSLIP once it is released). The ratings map to
    /// parameters through vehicle\levelmodifier.dat. The WheelColliders only carry the suspension: their friction is
    /// off while the model drives, and the horizontal velocity and yaw are written every physics step.
    /// Controls: W/S or arrows = throttle/brake (hold the brake at a standstill to reverse), A/D = steer,
    /// Shift (or Space) = drift, Ctrl = booster (needs a full gauge), R = reset; the client's default keys.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class DriftCar : MonoBehaviour
    {
        public WheelCollider[] Wheels = new WheelCollider[4]; // FL, FR, RL, RR
        public Transform[] WheelVisuals = new Transform[4];
        /// <summary>Each wheel model's turn relative to its collider pose (left wheels are flipped so rims face out).</summary>
        public Quaternion[] WheelVisualOffsets = { Quaternion.identity, Quaternion.identity, Quaternion.identity, Quaternion.identity };

        [Header("From the ratings (vehicle\\levelmodifier.dat)")]
        public float TopSpeedKmh = 236f;         // Speed -> 2000
        public float BoostTopSpeedKmh = 336f;    // Speed -> 2001
        public float ReverseTopSpeedKmh = 100f;  // Speed -> 2002
        public float EngineTorque = 191.9f;      // Accel -> 3000
        public float CornerDrag = 4867f;         // Accel -> 3001, N while turning wider than 30 m
        public float CornerDragBoosting = 1990f; // Accel -> 3002
        public float DriftExitGain = 0.884f;     // Accel -> 3003
        public float WallSpeedLoss = 99.7f;      // Crash -> 4000
        public float WallGaugeLoss = 75.9f;      // Crash -> 4005
        public float CarGaugeLoss = 59.8f;       // Crash -> 4004 (gauge loss hitting cars)
        public float UphillSpeedLoss;            // Crash -> 4012
        public float CrashKeepSpeedKmh = 100f;   // Crash -> 4014
        public float BoostDuration = 2.85f;      // Boost -> 5000, seconds per full gauge
        public float BoostForce = 2.95f;         // Boost -> 5001, engine force multiplier
        public float BoostFillSeconds = 70f;     // Boost -> 5002, empty to full while just driving
        /// <summary>Steering lock in degrees; keyboard steering reaches it instantly in the original.</summary>
        public float MaxSteer = 45f;
        /// <summary>vehiclelist CamN / CamH: extra chase-camera distance and height for this model.</summary>
        public float CamN, CamH;
        /// <summary>WheelCollider brakes, only used while the car is held (the model never brakes through the wheels).</summary>
        public float BrakeTorque = 4000f;

        [Header("Assists, Battle Zone and items (PHYSICS.md section 13)")]
        /// <summary>The car's assist options (option / set parts; Game/CarAssists.cs): written into the dynamics by 0x6d2580.</summary>
        public Game.CarAssists Assists = new Game.CarAssists();
        /// <summary>Battle Zone car (+0x1a9c, set by the PvP stages 0x7254dd / 0x7616fd): battle top speeds, battle fill, the
        /// gauge multiplier and no gauge loss from hits.</summary>
        public bool PvpMode;
        /// <summary>+0x1ab8 (setter 0x4786c0): every gauge change in the Battle Zone is multiplied by it.</summary>
        public float PvpGaugeGain = 1f;
        /// <summary>+0x1abc (setter 0x478750, only 1 or 2): the Battle Zone passive fill rate (the Boost Recharger doubles it).</summary>
        public float PvpFillRate = 1f;
        /// <summary>+0x8d8: battle combos, each adding the "BA_CB" fill.</summary>
        public int BattleCombos;
        /// <summary>0x4ccf20: the Drift City service (nation 1 / 4) takes 80 % of the km/h adds (0x47fde0, 0x476f90).</summary>
        public static float ServiceSpeedFactor = 0.8f;
        /// <summary>+0x1ad0: the Crash Capsule is running (+5 gauge per traffic hit, 0x47e510).</summary>
        public bool CrashCapsule;
        /// <summary>Invincible Rush seconds left: hits cost no speed and no durability.</summary>
        public float InvincibleTime;
        /// <summary>Durability left, 1 = new (the damage smoke shows below 0.4, EFFECTS.md "Damage smoke"; remake rule in section 13).</summary>
        public float Durability = 1f;
        /// <summary>Fired for each hit that counted (true = traffic / another car, false = a wall).</summary>
        public event System.Action<bool> Crashed;
        /// <summary>Fired when the car lands after a jump with the air time.</summary>
        public event System.Action<float> Landed;
        /// <summary>The 1 s mini boost (in[0x21b]) of Turbo Start / the post-drift booster.</summary>
        public float MiniBoostTimeLeft => miniBoost;
        /// <summary>The Turbo Stop brake (in[0x23e]) is pulling.</summary>
        public bool TurboStopping => stopTimer > 0f;

        [Header("Booster")]
        public float BoostGauge;          // 0..1 (the client's gauge is 0..100)
        public bool Boosting { get; private set; }
        /// <summary>Angle between heading and travel (degrees), braking and drifting state: read by CarSounds.</summary>
        public float SlipAngle { get; private set; }
        public bool Braking { get; private set; }
        /// <summary>True while drifting (FRSLIP or MTSLIP).</summary>
        public bool Handbrake { get; private set; }
        /// <summary>-1 reverse, 1..5 forward.</summary>
        public int Gear => gear;
        public float Rpm => rpm;
        /// <summary>0 NOSLIP (grip), 1 FRSLIP (drift button held), 2 MTSLIP (drifting on the steering).</summary>
        public int DriftState => state;
        public bool Airborne => airState == 1 || airState == 2;
        /// <summary>Mass of the camera's yaw spring: 4 right after a drift starts, settling back to 1 (0x6f5).</summary>
        public float CameraLag => camLag;
        public float BoostTimeLeft => boostTimer;
        public float AirTime => airTime;

        /// <summary>
        /// Set by AI/race scripts, the shop and countdowns to take the car away from the keyboard. Turning it on
        /// drops throttle and boost and holds the brakes, so a frozen car can't keep accelerating on the last input.
        /// </summary>
        public bool ExternalControl
        {
            get => externalControl;
            set
            {
                externalControl = value;
                if (value) Hold();
            }
        }
        bool externalControl;

        /// <summary>Races put a respawn point on the track here (R would otherwise go back to the city spawn).</summary>
        public System.Func<(Vector3 pos, Quaternion rot)?> RespawnOverride;

        /// <summary>Ground speed; like the client it ignores the vertical part.</summary>
        public float SpeedKmh => body ? new Vector2(body.linearVelocity.x, body.linearVelocity.z).magnitude * 3.6f : 0f;

        // ---- constants of the original model (addresses in PHYSICS.md) ----
        const float Tick = 0.003f;                 // 3 ms fixed step
        const float Mass = 800f;                   // ODE body mass
        const float WheelBase = 2.8f, CgHeight = 0.25f, CgToAxle = 1.4f, G = 9.8f;
        const float CorneringStiffness = 10000f;
        const float Understeer = 0.2f, UndersteerSliding = 0.03f, MinRadius = 3f;
        const float GripSlipLimit = 10f * Mathf.Deg2Rad, SlipLimit = 1.9198622f; // 10 and 110 degrees
        const float DriftSteerGain = 1.2f, DriftTurnSlip = 45f * Mathf.Deg2Rad;
        const float DriftMinSpeed = 70f / 3.6f, DriftKeepSpeed = 40f / 3.6f, DriftEndAngle = 0.2617994f;
        const float CoastForce = 2000f, LimiterForce = 20000f, BrakeForce = 25000f;
        const float SlowSurfaceSpeed = 150f / 3.6f, SlowSurfaceDrag = 5000f;
        Game.CarRoadState road;
        const float DriftDrag = 8000f, DriftDragSpeed = 60f / 3.6f, SlideFriction = 300f, SpinDrag = 500f;
        const float CornerDragRadius = 30f, ReverseTorque = 180f, ReverseDragSpeed = 20f / 3.6f;
        const int TopGear = 5;
        const float FirstGearRpm = 6500f, TopGearRpm = 7000f, FirstGearShare = 0.1838f;
        const float RpmCut = 7000f, RpmCutRange = 3000f, RpmMax = 8000f, ShiftMargin = 400f;
        const float RpmToRad = 0.10471976f;        // 2 pi / 60
        const float BoostDelay = 0.2f, GaugeMax = 100f;

        /// <summary>The client's second-order follower: x'' = (-(x - target) k - d x') / m, semi-implicit Euler (0x476800).</summary>
        struct Spring
        {
            public float M, D, K, X, V;
            public void Set(float m, float d, float k) { M = Mathf.Max(m, 0.001f); D = d; K = k; }
            public void Step(float target, float dt)
            {
                V += (-(X - target) * K - D * V) / M * dt;
                X += V * dt;
            }
            public void Reset(float x = 0f) { X = x; V = 0f; }
        }

        Rigidbody body;
        Vector3 resetPos;
        Quaternion resetRot;
        bool resetRequested;

        // model state (client field offsets in PHYSICS.md)
        Vector2 vel;                     // horizontal velocity (x, z)
        float heading, velAngle, slip;   // yaw of the car, of the body (travel direction, reversed in reverse gear), their difference
        Spring slipF, latF, yawF, rollF, pitchF;
        float latOut, turnRadius = 1e10f, slipT, latT, yawT, filterMass = 1f, rollMass = 1f, camLag = 1f;
        int state, driftDir;             // driftDir: +1 right, -1 left
        float driftSteer, driftHeading, exitGain, recoveryTimer;
        bool mtRecover, mtRecoverStarted, driftArmed = true;
        int snap;                        // straightening lock: +-1 armed, +-2 holding the heading
        float snapHeading, snapLat, snapTimer, prevHeading;
        float boostTimer, boostDelay, boostUsedGauge, reverseHold;
        // assists (0x479c90 / 0x4809c0): mini boost in[0x21b], Turbo Stop in[0x23e], the post-drift window +0x884
        float miniBoost, stopTimer, postDriftWindow, driftReleasedAt = -10f, driftPeakSlip, lastDamageClock = -10f;
        bool prevThrottle, prevBrake, prevDrift, wasBoosting;
        const float MiniBoostTime = 1f, TurboStopTime = 1f, TurboStopScale = 40f;   // in[0x45], in[0x46], in[0x712]
        int gear = 1;
        float rpm, longForce, driftTime, airTime, jumpCooldown, clock;
        Vector3 lastJumpPos;
        readonly float[] gearRpm = new float[TopGear];   // rpm per m/s in each gear
        float reverseRpm, downshiftRpm;

        // what the physics step did since the model last ran
        bool synced, modelDriving, wheelsConfigured, hitWall, hitObject, pendingCrash, hitObjectEnter;
        Vector2 wallNormal, objectNormal;
        float lastHitClock = -10f, lastGaugeHitClock = -10f, appliedYaw;
        Vector3 appliedPos;
        int idleSteps;
        int airState, groundedWheels;    // airState: 0 ground, 1 front wheels up, 2 flying, 3 landing
        float landTimer, bodyPitch;
        Vector3 groundNormal = Vector3.up;
        readonly float[] wheelFriction = new float[8];
        readonly RaycastHit[] rayHits = new RaycastHit[8];

        // visuals
        Transform bodyVisual;
        Quaternion bodyBaseRot;
        float steerInput, steerVisual, steerVisualVel, wheelSpin;

        float Gauge { get => BoostGauge * GaugeMax; set => BoostGauge = Mathf.Clamp01(value / GaugeMax); }

        /// <summary>Maps the game's car ratings (Accel/Speed/Crash/Boost, levels 1..999) onto the handling.</summary>
        public void ApplyStats(int accel, int speed, int crash, int boost)
        {
            TopSpeedKmh = LevelTable.Get(2000, speed);
            BoostTopSpeedKmh = LevelTable.Get(2001, speed);
            ReverseTopSpeedKmh = LevelTable.Get(2002, speed);
            EngineTorque = LevelTable.Get(3000, accel);
            CornerDrag = LevelTable.Get(3001, accel);
            CornerDragBoosting = LevelTable.Get(3002, accel);
            DriftExitGain = LevelTable.Get(3003, accel);
            WallSpeedLoss = LevelTable.Get(4000, crash);
            WallGaugeLoss = LevelTable.Get(4005, crash);
            CarGaugeLoss = LevelTable.Get(4004, crash);
            if (CarGaugeLoss <= 0f) CarGaugeLoss = WallGaugeLoss * 0.79f;   // samples only (levels 1 / 500 / 999: 59.8 / 41.6 / 40.4)
            UphillSpeedLoss = LevelTable.Get(4012, crash);
            CrashKeepSpeedKmh = LevelTable.Get(4014, crash);
            BoostDuration = LevelTable.Get(5000, boost);
            BoostForce = LevelTable.Get(5001, boost);
            BoostFillSeconds = Mathf.Max(0.1f, LevelTable.Get(5002, boost));
            // the player's car takes its assists with its ratings (0x6dbc40 re-applies them with the car data)
            var flow = Game.GameFlow.Instance;
            if (flow && flow.PlayerCar == this) { try { Assists = Game.CarAssists.Current; } catch (System.Exception ex) { Debug.LogWarning("assists: " + ex.Message); } }
            if (!body) body = GetComponent<Rigidbody>();
            if (body) body.mass = Mass;
            UpdateGearing();
            TuneSuspension();
        }

        /// <summary>Springs sized to the car's weight (about 1.8 Hz per corner) so heavy cars don't sit on the bump stops.</summary>
        public void TuneSuspension()
        {
            if (!body) body = GetComponent<Rigidbody>();
            if (!body) return;
            float corner = body.mass / 4f;
            float k = corner * Mathf.Pow(2f * Mathf.PI * 1.8f, 2f);
            foreach (var w in Wheels)
            {
                if (!w) continue;
                w.suspensionDistance = 0.25f;
                var sp = w.suspensionSpring;
                sp.spring = k;
                sp.damper = 2f * 0.38f * Mathf.Sqrt(k * corner);
                sp.targetPosition = 0.5f;
                w.suspensionSpring = sp;
            }
        }

        /// <summary>
        /// Five gears spaced geometrically: first reaches 6500 rpm at 18.38% of the top speed, fifth 7000 rpm at the
        /// top speed (0x4758d0). Kept as rpm per m/s; the tyre radius cancels out of both rpm and drive force.
        /// </summary>
        void UpdateGearing()
        {
            float vmax = Mathf.Max(1f, TopSpeedKmh / 3.6f);
            float first = FirstGearRpm / (FirstGearShare * vmax), top = TopGearRpm / vmax;
            float q = 1f / Mathf.Pow(first / top, 0.25f);
            for (int i = 0; i < TopGear; i++) gearRpm[i] = first * Mathf.Pow(q, i);
            reverseRpm = TopGearRpm / Mathf.Max(1f, ReverseTopSpeedKmh / 3.6f);
            downshiftRpm = (int)(FirstGearRpm * q) - ShiftMargin;
        }

        /// <summary>No drive, no boost, brakes on (used when the car is taken away from the player).</summary>
        void Hold()
        {
            Boosting = false;
            boostTimer = boostDelay = 0f;
            SetDriveMode(false);
        }

        /// <summary>
        /// Model mode turns the wheel friction off (the model owns the horizontal motion); held mode restores it with
        /// the brakes on so a parked car stays put.
        /// </summary>
        void SetDriveMode(bool model)
        {
            if (!wheelsConfigured)
            {
                bool any = false;
                for (int i = 0; i < 4; i++)
                {
                    if (!Wheels[i]) continue;
                    any = true;
                    wheelFriction[i * 2] = Wheels[i].forwardFriction.stiffness;
                    wheelFriction[i * 2 + 1] = Wheels[i].sidewaysFriction.stiffness;
                }
                if (!any) return;
                wheelsConfigured = true;
            }
            else if (modelDriving == model) return;
            modelDriving = model;
            for (int i = 0; i < 4; i++)
            {
                var w = Wheels[i];
                if (!w) continue;
                var f = w.forwardFriction; f.stiffness = model ? 0f : wheelFriction[i * 2]; w.forwardFriction = f;
                var s = w.sidewaysFriction; s.stiffness = model ? 0f : wheelFriction[i * 2 + 1]; w.sidewaysFriction = s;
                w.motorTorque = 0f;
                w.brakeTorque = model ? 0f : BrakeTorque;
            }
        }

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            body.mass = Mass;
            body.maxAngularVelocity = 50f;
            resetPos = transform.position;
            resetRot = transform.rotation;
            heading = transform.eulerAngles.y * Mathf.Deg2Rad;
            ApplyStats(1, 1, 1, 1);
        }

        void Start()
        {
            TuneSuspension();
            bodyVisual = transform.Find("Body");
            if (bodyVisual) bodyBaseRot = bodyVisual.localRotation;
        }

        void FixedUpdate()
        {
            idleSteps++;
            if (ExternalControl)
            {
                resetRequested = false;
                // nobody is driving it (countdown, shop): keep it parked on its brakes
                if (idleSteps > 3) { SetDriveMode(false); ClearHits(); }
                return;
            }
            if (Net.ChatBox.Typing) { resetRequested = false; ApplyControls(0, 0, false, Time.fixedDeltaTime); return; }
            ReadInput(out float throttle, out float steer, out bool drift);
            if (resetRequested) { resetRequested = false; Respawn(); return; }
            ApplyControls(throttle, steer, drift, Time.fixedDeltaTime, ReadBoost());
        }

        /// <summary>The booster key (Ctrl by default; OPTION 3000000 rebinds it: Game/Options/KeyBindings.cs).</summary>
        static bool ReadBoost()
        {
            Game.KeyBindings.ReadDriving(out _, out _, out _, out bool boost);
            return boost;
        }

        /// <summary>
        /// One physics step of driver input; also used by AI drivers and the headless physics test.
        /// throttle &lt; 0 is the brake, handbrake is the drift button, boost the booster button.
        /// </summary>
        public void ApplyControls(float throttle, float steerInput, bool handbrake, float dt, bool boost = false)
        {
            if (!body) body = GetComponent<Rigidbody>();
            if (dt <= 0f) return;
            idleSteps = 0;
            SetDriveMode(true);
            SyncFromBody();
            float accel = Mathf.Clamp01(throttle), brake = Mathf.Clamp01(-throttle);
            this.steerInput = Mathf.Clamp(steerInput, -1f, 1f);
            int n = Mathf.Max(1, Mathf.RoundToInt(dt / Tick));
            for (int i = 0; i < n; i++) Step(dt / n, accel, brake, this.steerInput, handbrake, boost);
            ApplyToBody(dt);
            ClearHits();
            SyncDamage();
        }

        void ClearHits()
        {
            hitWall = hitObject = hitObjectEnter = false;
            wallNormal = objectNormal = Vector2.zero;
        }

        /// <summary>Reads back what the physics step did (collisions, suspension) and the ground under the wheels.</summary>
        void SyncFromBody()
        {
            var v = body.linearVelocity;
            vel = new Vector2(v.x, v.z);
            float yaw = BodyYaw();
            var f = body.rotation * Vector3.forward;
            bodyPitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f));
            // a respawn, race teleport or the shop moved the car: restart the model where it now stands
            if (!synced || Mathf.Abs(Mathf.DeltaAngle(yaw * Mathf.Rad2Deg, appliedYaw * Mathf.Rad2Deg)) > 30f || (body.position - appliedPos).sqrMagnitude > 100f)
                ResetModel(yaw);
            // the body faces its velocity, so a hit that throws the car backwards in a forward gear would flip the
            // heading; drop the reversed part instead
            var travel = Dir(velAngle);
            float back = Vector2.Dot(vel, travel);
            if (gear >= 1 && back < 0f && vel.sqrMagnitude > 0.01f) vel -= travel * back;
            if (hitWall || hitObject) pendingCrash = true;
            UpdateGround();
        }

        void ResetModel(float yaw)
        {
            heading = prevHeading = snapHeading = driftHeading = yaw;
            velAngle = vel.magnitude >= 0.1f ? WrapPi(Mathf.Atan2(vel.x, vel.y) + (gear < 0 ? Mathf.PI : 0f)) : yaw;
            slip = WrapPi(heading - velAngle);
            slipF.Reset(slip); latF.Reset(); yawF.Reset();
            latOut = 0f; slipT = latT = yawT = 0f; turnRadius = 1e10f;
            state = 0; driftDir = 0; snap = 0; recoveryTimer = 0f; filterMass = 1f;
            mtRecover = mtRecoverStarted = false;
            synced = true;
        }

        /// <summary>
        /// Airborne state like the client's per-wheel heights (0x191): both front wheels more than 0.5 m up = 1,
        /// all four = 2, the first touch down = 3 (landing) until everything settles.
        /// </summary>
        void UpdateGround()
        {
            groundedWheels = 0;
            float h0 = 0f, h1 = 0f, h2 = 0f, h3 = 0f;
            var normals = Vector3.zero;
            bool any = false;
            for (int i = 0; i < 4; i++)
            {
                var w = Wheels[i];
                if (!w) continue;
                any = true;
                float h = 0f;
                if (w.GetGroundHit(out var hit)) { groundedWheels++; normals += hit.normal; }
                else h = HeightAboveGround(w);
                if (i == 0) h0 = h; else if (i == 1) h1 = h; else if (i == 2) h2 = h; else h3 = h;
            }
            groundNormal = groundedWheels > 0 ? normals.normalized : Vector3.up;
            if (!any) { airState = 0; return; }
            if (airState == 0 && h0 > 0.5f && h1 > 0.5f) airState = 1;
            if (airState == 1 && h0 > 0.5f && h1 > 0.5f && h2 > 0.5f && h3 > 0.5f) airState = 2;
            if ((airState == 1 && (h0 <= 0f || h1 <= 0f)) || (airState == 2 && (h0 <= 0f || h1 <= 0f || h2 <= 0f || h3 <= 0f)))
            {
                airState = 3;
                landTimer = 0f;
            }
            if (airState == 3)
            {
                landTimer = groundedWheels >= 4 ? landTimer + Time.fixedDeltaTime : 0f;
                if (landTimer > 0.15f) airState = 0;
            }
        }

        float HeightAboveGround(WheelCollider w)
        {
            var origin = w.transform.position;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, rayHits, 30f, ~0, QueryTriggerInteraction.Ignore);
            float best = 30f;
            for (int i = 0; i < count; i++)
                if (rayHits[i].collider.attachedRigidbody != body && rayHits[i].distance < best) best = rayHits[i].distance;
            return best - w.suspensionDistance - w.radius;
        }

        /// <summary>Writes the model's horizontal velocity and heading into the rigidbody; vertical motion stays physical.</summary>
        void ApplyToBody(float dt)
        {
            body.linearVelocity = new Vector3(vel.x, body.linearVelocity.y, vel.y);
            var w = body.angularVelocity;
            float yawError = Mathf.DeltaAngle(BodyYaw() * Mathf.Rad2Deg, heading * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            body.angularVelocity = new Vector3(w.x, yawError / dt, w.z);
            appliedYaw = heading;
            appliedPos = body.position;
            if (groundedWheels >= 2 && groundNormal.y > 0.3f)
            {
                // frictionless wheels would let gravity slide the car down slopes; the model has its own slope force
                float g = -Physics.gravity.y;
                body.AddForce(new Vector3(groundNormal.x, 0f, groundNormal.z) * (-body.mass * g * groundNormal.y), ForceMode.Force);
            }
            if (airState == 2)
            {
                // in the air the client drops the front wheels at 14 m/s^2 and the rear ones at 10, so the nose dips
                Vector3 front = WheelCentre(0, 1, transform.forward * CgToAxle), rear = WheelCentre(2, 3, -transform.forward * CgToAxle);
                float g = -Physics.gravity.y;
                body.AddForceAtPosition(Vector3.down * (body.mass * 0.5f * (14f - g)), front, ForceMode.Force);
                body.AddForceAtPosition(Vector3.down * (body.mass * 0.5f * (10f - g)), rear, ForceMode.Force);
            }
        }

        Vector3 WheelCentre(int a, int b, Vector3 fallback)
        {
            if (Wheels[a] && Wheels[b]) return (Wheels[a].transform.position + Wheels[b].transform.position) * 0.5f;
            return body.worldCenterOfMass + fallback;
        }

        float BodyYaw()
        {
            var f = body.rotation * Vector3.forward;
            if (f.x * f.x + f.z * f.z < 1e-4f) return heading;
            return Mathf.Atan2(f.x, f.z);
        }

        /// <summary>One 3 ms tick of the client update (input 0x479c90, then 0x4809c0 in its own order).</summary>
        void Step(float dt, float accel, float brakeIn, float steerIn, bool driftButton, bool boostButton)
        {
            clock += dt;
            float speed = vel.magnitude;
            if (Mathf.Abs(snap) == 2) { snapTimer += dt; if (snapTimer > 1f) { snapTimer = 0f; snap = 0; } }
            else snapTimer = 0f;
            if (jumpCooldown > 0f) jumpCooldown = Mathf.Max(0f, jumpCooldown - dt);

            // ---- input ----
            if (gear < 0) { float t = accel; accel = brakeIn; brakeIn = t; }   // in reverse the brake key drives
            float throttle = accel >= 0.1f ? accel : 0f;
            float brake = accel < 0.1f ? brakeIn : 0f;
            float steer = steerIn * MaxSteer * Mathf.Deg2Rad;                   // + = right
            if (boostButton && Gauge >= GaugeMax && throttle >= 0.1f && brake < 0.1f && gear > 0 && boostTimer <= 0f && boostDelay <= 0f)
            {
                boostDelay = BoostDelay;
                boostUsedGauge = Gauge;
                Gauge = 0f;
            }
            // the keys as pressed (in reverse the swap above exchanged them)
            AssistInput(dt, ref speed, (gear < 0 ? brakeIn : accel) >= 0.1f, (gear < 0 ? accel : brakeIn) >= 0.1f, driftButton);
            if (throttle >= 0.1f || brake < 0.1f) { brake = 0f; reverseHold = 0f; }
            else
            {
                // holding the brake at a standstill for 0.4 s changes direction and rolls off at 1 m/s
                if (speed < 0.1f && reverseHold <= 0f) reverseHold = 0.4f;
                if (reverseHold > 0f)
                {
                    if (reverseHold < dt)
                    {
                        var nose = Dir(heading);
                        if (gear < 0) { gear = 1; vel = nose; }
                        else { gear = -1; vel = -nose; }
                        velAngle = heading;
                        speed = 1f; throttle = brake = 0f; reverseHold = 0f;
                    }
                    else reverseHold -= dt;
                }
            }
            // the drift button starts a slide when pressed while steering above 70 km/h; it re-arms on release
            if (!driftButton) driftArmed = true;
            else if (driftArmed && speed > DriftMinSpeed && Mathf.Abs(steer) >= 0.1f && state == 0 && gear >= 1 && recoveryTimer <= 0f)
            {
                state = 1;
                filterMass = 3f; rollMass = 4f; camLag = 4f;
                mtRecover = mtRecoverStarted = false;
                driftSteer = steer;
                driftDir = steer >= 0f ? 1 : -1;
                if (latF.X * driftDir < 0f) latF.Reset();
                exitGain = 1f; snap = 0; snapLat = 0f;
                driftArmed = false;
            }

            // ---- drift state changes ----
            float slipNow = WrapPi(heading - velAngle);
            if (state == 1)
            {
                if ((!driftButton && Mathf.Abs(slip) > 0f) || speed < DriftKeepSpeed)
                {
                    state = 2;
                    filterMass = 6f;
                    slipF.V = 0f; yawF.V = 0f; latF.V = 0f;
                    driftHeading = heading;
                }
            }
            else if (state == 2 && (Mathf.Abs(slipNow) < DriftEndAngle || speed < 1f))
            {
                // back to grip: freeze the heading and swing the travel into it (straightening lock)
                filterMass = 1.6f;
                state = 0;
                recoveryTimer = Mathf.Abs(LateralSpeed()) * 0.2f / (40000f / Mass);
                slip = slipNow; slipF.Reset(slipNow);
                snapLat = slipNow * 60f;
                latOut = 0f; latF.Reset();
                snap = driftDir * 2; snapHeading = heading;
                OnDriftEnded();
            }
            if (state != 0) brake = 0f;
            if (filterMass > 1f) filterMass = Mathf.Max(1f, filterMass - dt * 6f);
            if (camLag > 1f) camLag = Mathf.Max(1f, camLag - dt * 20f);
            if (rollMass > 1f) rollMass = Mathf.Max(1f, rollMass - dt * 5f);
            if (pendingCrash)
            {
                pendingCrash = false;
                lastHitClock = clock;
                if (state != 0)
                {
                    // hitting anything while sliding ends the drift and keeps at most the Crash rating's speed (4014)
                    state = 0; filterMass = 2f;
                    float keep = Mathf.Min(speed, CrashKeepSpeedKmh / 3.6f);
                    vel = Dir(heading) * keep; speed = keep; velAngle = heading;
                    slip = 0f; slipF.Reset(); latF.Reset(); yawF.Reset(); latOut = 0f;
                    snapHeading = heading; driftDir = 0;
                    driftTime = 0f;
                }
            }
            if (snap != 0 && clock - lastHitClock < 0.3f) snap = 0;
            if (recoveryTimer > 0f)
            {
                if (slip * driftDir < 0f) recoveryTimer = 0f;
                if (recoveryTimer <= dt) { recoveryTimer = 0f; if (state == 0) driftDir = 0; }
                else if (Mathf.Abs(ForwardSpeed()) >= 1f) recoveryTimer -= dt;
                else recoveryTimer = 0f;
            }

            // ---- booster: 0.2 s after the press it burns for BoostDuration while the throttle is held ----
            // passive fill (0x480f94): city (CH_BT - 1) + field_b_r; Battle Zone (combos x BA_CB + battle_b_r) x +0x1abc
            float fillRate = PvpMode ? (BattleCombos * Assists.BattleComboFill + Assists.BattleFill) * PvpFillRate
                                     : Assists.ChaseFill - 1f + Assists.FieldFill;
            AddGauge(dt * GaugeMax / BoostFillSeconds * fillRate, false);
            if (boostDelay <= 0f)
            {
                if (boostTimer > 0f)
                {
                    boostTimer = boostTimer > dt && throttle >= 0.1f ? boostTimer - dt : 0f;
                    // field_b_fr (in[0x251]): a chance of the full gauge back when a boost ends (city only)
                    if (boostTimer <= 0f && !PvpMode && Assists.FieldBoostRefill > 0f && Random.Range(0, 101) < Assists.FieldBoostRefill) AddGauge(GaugeMax, true);
                }
            }
            else if (boostDelay > dt) boostDelay -= dt;
            else { boostTimer = (BoostDuration + Assists.BoostTimeAdd) / GaugeMax * boostUsedGauge; boostDelay = 0f; }
            bool boosting = boostTimer > 0f;
            if (miniBoost > 0f) miniBoost = Mathf.Max(0f, miniBoost - dt);
            if (stopTimer > 0f) stopTimer = brake < 0.1f ? 0f : Mathf.Max(0f, stopTimer - dt);
            if (postDriftWindow > 0f) postDriftWindow = Mathf.Max(0f, postDriftWindow - dt);
            if (InvincibleTime > 0f) InvincibleTime = Mathf.Max(0f, InvincibleTime - dt);
            bool engineBoost = boosting || miniBoost > 0f;

            float m = filterMass;
            if (state == 0) { slipF.Set(m, 15f, 20f / m); latF.Set(m, 10f, 35f / m); yawF.Set(m, 15f, 30f / m); }
            else { slipF.Set(m, 15f, 30f / m); latF.Set(m, 10f, 15f / m); yawF.Set(m, 25f, 10f / m); }

            // ---- force along the direction of travel ----
            // Turbo Stop (in[0x23e]): 40 x the coast force instead of the engine while it runs above 10 km/h
            float F = stopTimer > 0f && speed > 10f / 3.6f ? TurboStopScale * -CoastForce
                    : throttle <= 0f ? -CoastForce : DriveForce(boosting, engineBoost) * throttle;
            // the limiter (no limiter while boosting or on a mini boost); an empty Mittron tank caps the car at 150 km/h (0x476f90)
            if (!engineBoost && NormalTop() < speed) F -= LimiterForce;
            if (brake > 0f) F -= BrakeForce * brake;
            if (airState == 2) F = -CoastForce;
            if (Mathf.Abs(bodyPitch) >= Mathf.Deg2Rad && state == 0)
            {
                float slope = 1000f * bodyPitch * (bodyPitch <= 0f ? 10f : 3f);
                F += gear < 1 ? slope : -slope;
            }

            // ---- steering and drifting ----
            float latSpeed = LateralSpeed();
            Vector2 fwdDir = Dir(heading), rightDir = Right(fwdDir);
            Vector2 extra = Vector2.zero;   // world-space forces
            float bodyForce = 0f;           // along the body x axis (travel direction; the nose in reverse)
            bool counter = false;
            bool straight = (Mathf.Abs(steer) < 0.1f && state == 0) || ((recoveryTimer != 0f || Mathf.Abs(steer) < 0.1f) && Mathf.Abs(snap) == 2);
            if (straight)
            {
                if (snap == 0) snap = driftDir;
                if (state == 0)
                {
                    if (recoveryTimer > 0f) extra += RecoveryForce(latSpeed * 4f * 500f, fwdDir, rightDir);
                    driftDir = 0;
                    turnRadius = 1e10f;
                    yawT = latT = slipT = 0f;
                    if (Mathf.Abs(snap) == 2) latT = snapLat;
                }
            }
            else
            {
                snap = 0;
                if (state == 0)
                {
                    if (recoveryTimer > 0f) extra += RecoveryForce(latSpeed * SlideFriction * 4f, fwdDir, rightDir);
                    driftDir = steer >= 0f ? 1 : -1;
                    turnRadius = (Understeer * speed * speed / G + WheelBase) / steer;
                    if (Mathf.Abs(turnRadius) < MinRadius) turnRadius = turnRadius <= 0f ? -MinRadius : MinRadius;
                    yawT = speed / turnRadius;
                    latT = speed * speed / turnRadius;
                    slipT = Mathf.Clamp(RearSlip(speed), -GripSlipLimit, GripSlipLimit);
                    if (gear >= 1 && Mathf.Abs(turnRadius) > CornerDragRadius) bodyForce -= boosting ? CornerDragBoosting : CornerDrag;
                    else if (gear < 0 && speed > ReverseDragSpeed) bodyForce += speed / ReverseDragSpeed * 500f + 4000f;
                }
                else if (state == 1)
                {
                    // FRSLIP: steering locked at 1.2x the entry angle, no engine, 8000 N of drag (24000 counter-steering)
                    counter = (driftDir > 0 && steer < 0f) || (driftDir <= 0 && steer > 0f);
                    steer = driftSteer * DriftSteerGain;
                    turnRadius = (UndersteerSliding * speed * speed / G + WheelBase) / steer;
                    if (Mathf.Abs(slip) >= DriftTurnSlip) { yawT = speed / turnRadius * 0.25f; latT = speed * speed / turnRadius * 0.5f; }
                    else yawT = latT = 0f;
                    if (F > 0f) F = 0f;
                    slipT = Mathf.Clamp(RearSlip(speed) - CgToAxle / turnRadius, -SlipLimit, SlipLimit);
                    if (speed > DriftDragSpeed) bodyForce -= DriftDrag * (counter ? 3f : 1f);
                    if (latSpeed != 0f && speed <= DriftDragSpeed)
                        extra -= rightDir * (SlideFriction * latSpeed * (boosting ? 2f : 1f));
                }
                else
                {
                    // MTSLIP: keep steering into the slide to hold the angle; centre or counter-steer to straighten
                    bool wasRecovering = mtRecover;
                    if (!mtRecover && steer * driftDir < 0.1f) mtRecover = true;
                    steer = driftSteer;
                    if (speed < DriftKeepSpeed) mtRecover = true;
                    if (mtRecover && !mtRecoverStarted)
                    {
                        mtRecoverStarted = true;
                        slipF.V = 0f; filterMass = 1f; driftHeading = heading;
                    }
                    steer *= DriftSteerGain;
                    turnRadius = (UndersteerSliding * speed * speed / G + WheelBase) / steer;
                    if (!mtRecover)
                    {
                        yawT = 0.25f * speed / turnRadius;
                        latT = 0.5f * speed * speed / turnRadius;
                        slipT = Mathf.Clamp(slipF.X, -SlipLimit, SlipLimit);
                    }
                    else
                    {
                        yawT = latT = 0f;
                        slipT = WrapPi(driftHeading - velAngle);   // the nose stays put while the travel swings round
                    }
                    if (F > 0f) F = 0f;
                    if (!wasRecovering && mtRecover) exitGain = Mathf.Clamp01(Mathf.Abs(slip) / DriftTurnSlip);
                    if (latSpeed != 0f)
                    {
                        float side = SlideFriction * 4f * latSpeed;
                        extra -= rightDir * side;
                        // straightening turns the sideways speed back into forward speed (Accel rating 3003)
                        if (mtRecover) extra += fwdDir * (Mathf.Abs(side) * exitGain * DriftExitGain);
                    }
                    if (speed > 0f) extra -= MoveDir(speed) * SpinDrag;
                }
            }
            // off the road, on sand or in water above 150 km/h the client cuts the engine and adds 5000 N of drag (0x481cf5)
            if (road || TryGetComponent(out road))
                if (road.SlowSurface && speed > SlowSurfaceSpeed) { if (F > 0f) F = 0f; bodyForce -= SlowSurfaceDrag; }
            if (clock - lastHitClock < 0.3f && Mathf.Abs(latT) < Mathf.Abs(latF.X)) { latF.X = latT; latF.V = 0f; }
            // far above even the boost top speed the client drops every extra force
            if (SpeedLimit(Mathf.Min(100f / 3.6f + BoostTopSpeedKmh / 3.6f, Game.MittronFuel.TopSpeedCapKmh(this) / 3.6f), boosting) + SpeedAdd < speed) { bodyForce = 0f; extra = Vector2.zero; }
            if (counter) { slipT = slipF.X; slipF.V = 0f; latT = 0f; latF.Reset(); yawT = 0f; yawF.Reset(); }
            longForce = F;

            // ---- integrate ----
            if (dt / Mass * F + speed >= 0.1f || throttle >= 0.1f)
            {
                slipF.Step(slipT, dt);
                latF.Step(latT, dt);
                yawF.Step(yawT, dt);
                if (speed < ReverseDragSpeed && F < 0f)
                {
                    // slowing at walking pace: the sideways pull shrinks with speed
                    float pull = latT * speed / ReverseDragSpeed;
                    if (latT == 0f) latF.X = 0f;
                    if ((pull < 0f && pull > latF.X) || (pull > 0f && pull < latF.X)) latF.X = pull;
                }
                slip = Mathf.Clamp(slipF.X, -SlipLimit, SlipLimit);
                slipF.X = slip;
                latOut = latF.X;
                var move = MoveDir(speed);
                var bodyX = gear >= 1 ? move : -move;
                var force = F * move + bodyForce * bodyX + extra + Right(bodyX) * (latOut * Mass);
                vel += force / Mass * dt;
            }
            else
            {
                slipF.Reset(); latF.Reset(); yawF.Reset(); latOut = 0f;
                vel = Vector2.zero;
            }
            ApplyContacts(dt, speed);

            // ---- read back: the body faces its velocity, the car is the body turned by the slip angle ----
            speed = vel.magnitude;
            if (gear < 1)
            {
                snap = 0;
                if (speed >= 0.1f)
                {
                    float cap = Mathf.Max(SpeedLimit(ReverseTopSpeedKmh / 3.6f, boosting), ReverseTopSpeedKmh / 3.6f);
                    if (speed > cap) { vel *= cap / speed; speed = cap; }
                    velAngle = WrapPi(Mathf.Atan2(vel.x, vel.y) + Mathf.PI);
                    heading = WrapPi(slip + velAngle);
                }
                else StopTurning();
            }
            else if (speed >= 0.1f)
            {
                if (boosting)
                {
                    float cap = BoostTop();
                    if (speed > cap) { vel *= cap / speed; speed = cap; }
                }
                velAngle = Mathf.Atan2(vel.x, vel.y);
                if (Mathf.Abs(snap) == 2)
                {
                    heading = snapHeading;
                    slip = WrapPi(heading - velAngle);
                    slipF.Reset(slip);
                    if (Mathf.Abs(slip) < 0.0349066f) { snap = 0; snapLat = 0f; latF.Reset(); }
                }
                else heading = WrapPi(slip + velAngle);
                if (state == 2 && mtRecover && WrapPi(heading - driftHeading) * driftDir > 0f) heading = driftHeading;
            }
            else StopTurning();
            if (snap == 1 || snap == -1)
            {
                // armed: once the nose starts swinging back after a turn, freeze it and pull the travel in line
                snapHeading = heading;
                if (WrapPi(heading - prevHeading) * snap < 0f) { snapLat = WrapPi(heading - velAngle) * 60f; snap *= 2; }
            }
            prevHeading = heading;

            // ---- engine speed and the automatic gearbox ----
            if (airState == 1 || airState == 2)
            {
                rpm += dt * 15000f;
                if (rpm > 10300f + Random.Range(0, 1000)) rpm = 9700f - Random.Range(0, 1000);
            }
            else
            {
                rpm = Mathf.Clamp((gear < 0 ? reverseRpm : gear > 0 ? gearRpm[gear - 1] : 0f) * speed, 0f, RpmMax);
                if (gear > 0)
                {
                    if (rpm > FirstGearRpm + ShiftMargin && gear < TopGear) gear++;
                    if (rpm < downshiftRpm && gear > 1 && brake < 0.1f) gear--;
                }
            }

            // ---- booster gauge rewards: drifts and jumps ----
            if (state != 0) { driftTime += dt; driftPeakSlip = Mathf.Max(driftPeakSlip, Mathf.Abs(WrapPi(heading - velAngle))); }
            else if (driftTime > 0f) OnDriftEnded();
            if (airState == 1 || airState == 2) airTime += dt;
            else if (airTime > 0f)
            {
                var pos = body.position;
                if (airTime > 0.4f && (jumpCooldown <= 0f || (pos - lastJumpPos).magnitude > 10f)) AddGauge(GaugeMax, true);
                jumpCooldown = 2f;
                lastJumpPos = pos;
                float t = airTime;
                airTime = 0f;
                try { Landed?.Invoke(t); } catch (System.Exception ex) { Debug.LogException(ex); }
            }
            wasBoosting = boosting;

            BodyTilt(dt, speed, boosting);
            Boosting = boosting;
            Braking = brake > 0f && speed > 1f;
            Handbrake = state != 0;
            SlipAngle = speed > 1f ? Mathf.Abs(slip) * Mathf.Rad2Deg : 0f;
        }

        void StopTurning()
        {
            heading = WrapPi(slip + velAngle);
            velAngle = heading;
            slip = 0f;
            slipF.Reset();
            snap = 0;
        }

        /// <summary>A drift longer than 0.4 s refills (t - 0.4) * 15 of the gauge, at most 15.</summary>
        void OnDriftEnded()
        {
            if (driftTime > 0.4f) AddGauge(Mathf.Min((driftTime - 0.4f) * 15f, 15f), true);
            // the post-drift booster (0x486745): a slide past 30 degrees opens a 0.3 s window for the mini boost
            if (Assists.DriftBooster && driftTime > 0.4f && driftPeakSlip > 30f * Mathf.Deg2Rad) postDriftWindow = 0.3f;
            driftTime = 0f;
            driftPeakSlip = 0f;
        }

        // ================= assists, Battle Zone, items (PHYSICS.md section 13) =================
        /// <summary>
        /// The gauge add of 0x47beb0: the passive fill only while no boost runs or waits; in the Battle Zone every change is
        /// multiplied by <see cref="PvpGaugeGain"/> (+0x1ab8); the gauge stays within 0..100.
        /// </summary>
        void AddGauge(float amount, bool reward)
        {
            if (!reward && (boostTimer > 0f || boostDelay > 0f)) return;
            if (PvpMode) amount *= PvpGaugeGain;
            Gauge += amount;
        }

        /// <summary>
        /// Turbo Start / Turbo Stop / the post-drift booster (0x479c90): the drift key pressed (or let go less than 0.5 s ago)
        /// with a fresh forward key while standing gives a 1 s mini boost (in[0x21b] = in[0x45]: the boost force without
        /// the boost top speed); with a fresh brake key while moving forward, a 1 s hard stop (in[0x23e] = in[0x46]).
        /// </summary>
        void AssistInput(float dt, ref float speed, bool fwdKey, bool backKey, bool driftKey)
        {
            bool fwdPressed = fwdKey && !prevThrottle, backPressed = backKey && !prevBrake, driftPressed = driftKey && !prevDrift;
            if (!driftKey && prevDrift) driftReleasedAt = clock;
            bool driftRecent = driftKey || clock - driftReleasedAt < 0.5f;
            prevThrottle = fwdKey; prevBrake = backKey; prevDrift = driftKey;
            if (Assists.TurboStart && driftRecent && fwdPressed && speed < 0.1f && boostTimer <= 0f)
            {
                if (gear < 0) gear = 1;   // the client turns a reversing car round to first gear
                miniBoost = MiniBoostTime;
                driftReleasedAt = -10f;
            }
            if (Assists.TurboStop && driftRecent && backPressed && gear >= 1 && speed > 0.1f)
            {
                stopTimer = TurboStopTime;
                driftReleasedAt = -10f;
            }
            if (Assists.DriftBooster && postDriftWindow > 0f && driftPressed && fwdKey && boostTimer <= 0f && state == 0)
            {
                miniBoost = MiniBoostTime;
                postDriftWindow = 0f;
            }
        }

        float SpeedAdd => Assists.SpeedKmh * ServiceSpeedFactor / 3.6f;   // car +0x920 (0x47fde0)

        /// <summary>Top speed for the limiter (0x476f90): slope, assists, the Battle Zone top speed (BA_NS / BA_MS), the fuel cap.</summary>
        float NormalTop()
        {
            float v = SpeedLimit(TopSpeedKmh / 3.6f, false) + SpeedAdd;
            if (PvpMode) v += Assists.BattleSpeedKmh * ServiceSpeedFactor / 3.6f;
            return Mathf.Min(v, Game.MittronFuel.TopSpeedCapKmh(this) / 3.6f);
        }

        /// <summary>The boost cap (0x476f90 boosting branch): in the Battle Zone the larger of boost top + BA_BM and normal top + BA_NS.</summary>
        float BoostTop()
        {
            float v = SpeedLimit(BoostTopSpeedKmh / 3.6f, true) + SpeedAdd;
            if (PvpMode)
            {
                float f = ServiceSpeedFactor / 3.6f;
                v = Mathf.Max(v + Assists.BattleBoostKmh * f, Assists.BattleSpeedKmh * f + SpeedAdd + SpeedLimit(TopSpeedKmh / 3.6f, false));
            }
            return Mathf.Min(v, Game.MittronFuel.TopSpeedCapKmh(this) / 3.6f);
        }

        /// <summary>Applies the car's assists (option / set parts) and the fuel tank's part bonuses.</summary>
        public void ApplyAssists(Game.CarAssists a) => Assists = a ?? new Game.CarAssists();

        /// <summary>The Emergency Boost Kit: a boost now with the current gauge (needs 30 %, i_n_00060).</summary>
        public bool StartBoostNow()
        {
            if (Gauge < 30f || boostTimer > 0f || boostDelay > 0f || gear < 1) return false;
            boostDelay = BoostDelay;
            boostUsedGauge = Gauge;
            Gauge = 0f;
            return true;
        }

        /// <summary>Repairs a share of the durability (1 = all: Gio, a full repair on crash; 0.3 the Mk.II kit).</summary>
        public void Repair(float share) { SyncDamage(); Durability = Mathf.Clamp01(Durability + share); SyncDamage(true); }

        Game.CarEffects effects;
        float damageWritten = -1f;

        /// <summary>
        /// Keeps CarEffects.Damage01 (the damage smoke) and <see cref="Durability"/> together: the repair items set Damage01
        /// directly, so a change made there is taken over first.
        /// </summary>
        void SyncDamage(bool force = false)
        {
            if (!effects) effects = GetComponentInChildren<Game.CarEffects>();
            if (!effects) return;
            if (!force && (damageWritten < 0f || Mathf.Abs(effects.Damage01 - damageWritten) > 1e-4f)) Durability = Mathf.Clamp01(1f - effects.Damage01);
            damageWritten = effects.Damage01 = Mathf.Clamp01(1f - Durability);
        }

        /// <summary>
        /// Durability [remake rule, section 13]: a hit costs min(v / vmax, 1) x the Crash rating's gauge loss (4005 walls,
        /// 4004 cars) / 1000 at most once a second, nothing during Invincible Rush. Below 40 % the car smokes (0x69e590).
        /// </summary>
        void Damage(float speedBefore, bool car)
        {
            if (InvincibleTime > 0f || clock - lastDamageClock < 1f) return;
            lastDamageClock = clock;
            float vmax = Mathf.Max(1f, TopSpeedKmh / 3.6f);
            Durability = Mathf.Clamp01(Durability - Mathf.Min(speedBefore / vmax, 1f) * (car ? CarGaugeLoss : WallGaugeLoss) / 1000f);
            SyncDamage();
        }

        /// <summary>A traffic car was hit (first contact, 0x47e510): Crash Capsule +5; city field_c_fb, Battle Zone battle_c_b /
        /// battle_c_fb / battle_c_fr.</summary>
        void TrafficHit()
        {
            if (CrashCapsule) AddGauge(5f, true);
            if (!PvpMode)
            {
                if (Assists.FieldCrashFull > 0f && Random.Range(0, 101) < Assists.FieldCrashFull) AddGauge(GaugeMax, true);
            }
            else
            {
                if (Assists.BattleCrashBoost > 0f) AddGauge(Assists.BattleCrashBoost, true);
                if (Assists.BattleCrashFull > 0f && Random.Range(0, 101) < Assists.BattleCrashFull) AddGauge(GaugeMax, true);
                if (Assists.BattleCrashRepair > 0f && Random.Range(0, 101) < Assists.BattleCrashRepair) Repair(1f);
            }
        }

        /// <summary>
        /// Contacts from the last physics step (client callback 0x47e510): walls bleed speed by the Crash rating and
        /// turn the travel along the wall at 270 deg/s; traffic bleeds a little; a wall hit costs gauge once a second.
        /// </summary>
        void ApplyContacts(float dt, float speedBefore)
        {
            if (!hitWall && !hitObject) return;
            float speed = vel.magnitude;
            if (speed < 0.1f) return;
            var dir = vel / speed;
            float vmax = TopSpeedKmh / 3.6f;
            // "untouchable" keeps the speed through hits while boosting (0x47c000: +0x91c); so does Invincible Rush
            bool keepSpeed = (Assists.Untouchable && boostTimer > 0f) || InvincibleTime > 0f;
            bool fast = speed > 0.7f * FirstGearShare * vmax && !keepSpeed;
            if (hitObjectEnter)
            {
                hitObjectEnter = false;
                TrafficHit();
                Damage(speedBefore, true);
                try { Crashed?.Invoke(true); } catch (System.Exception ex) { Debug.LogException(ex); }
            }
            if (hitWall)
            {
                var n = wallNormal.normalized;
                float impact = -Vector2.Dot(n, dir);
                if (impact < 0.1f) impact = 0f;
                // crashsaving / BA_CS scale the loss (in[0x249] = +0x924, 1 - percent)
                if (fast) vel *= Mathf.Max(0.1f, 1f - dt * Assists.CrashLoss * WallSpeedLoss * impact);
                if (state == 0 && gear >= 1 && Vector2.Dot(Dir(heading), -n) > 0f)
                {
                    var along = dir - Vector2.Dot(dir, n) * n;
                    if (along.sqrMagnitude > 1e-6f)
                    {
                        float a = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, b = Mathf.Atan2(along.x, along.y) * Mathf.Rad2Deg;
                        vel = Dir(Mathf.MoveTowardsAngle(a, b, 270f * dt) * Mathf.Deg2Rad) * vel.magnitude;
                    }
                }
                if (clock - lastGaugeHitClock > 1f)
                {
                    lastGaugeHitClock = clock;
                    // no gauge loss from hits in the Battle Zone (0x47c000 / 0x47e510 test +0x1a9c)
                    if (!PvpMode) AddGauge(-Mathf.Min(speedBefore / vmax, 1f) * WallGaugeLoss, true);
                    if (impact > 0f) { Damage(speedBefore, false); try { Crashed?.Invoke(false); } catch (System.Exception ex) { Debug.LogException(ex); } }
                }
            }
            if (hitObject && fast)
            {
                float impact = -Vector2.Dot(objectNormal.normalized, dir);
                if (impact < 0.1f) impact = 0f;
                vel *= Mathf.Max(0.1f, 1f - dt * 2f * impact);
            }
        }

        /// <summary>Damps the sideways speed for a moment after a drift and feeds part of it forward (0x274).</summary>
        Vector2 RecoveryForce(float side, Vector2 fwdDir, Vector2 rightDir) =>
            -rightDir * side + fwdDir * (Mathf.Abs(side) * exitGain * DriftExitGain);

        /// <summary>Rear tyre slip angle of the steady-state turn: v^2 * rear axle load / (R g C).</summary>
        float RearSlip(float speed) => speed * speed * (Mass * G * CgToAxle / WheelBase) / (turnRadius * G) / CorneringStiffness;

        /// <summary>
        /// Drive force at the wheels: torque * gear ratio / tyre radius = torque * rpm-per-m/s * 2pi/60. Above 7000 rpm
        /// the torque fades out by 10000; boosting in top gear skips that and multiplies by the Boost rating.
        /// </summary>
        float DriveForce(bool boosting, bool engineBoost)
        {
            float torque, ratio;
            // reverse: bonusreal adds to the reverse torque (0x475840: (1 + +0x92c) x in[0xa2])
            if (gear < 1) { torque = ReverseTorque * (1f + Assists.ReverseAccel); ratio = reverseRpm; }
            else
            {
                ratio = gearRpm[gear - 1];
                torque = boosting && gear == TopGear ? EngineTorque
                    : rpm > RpmCut ? EngineTorque * (1f - Mathf.Min(rpm - RpmCut, RpmCutRange) / RpmCutRange) : EngineTorque;
            }
            // the boost force also applies on a mini boost (0x476e30: +0x868 or +0x86c), the top-gear fade only on a real boost
            return torque * ratio * RpmToRad * (engineBoost ? BoostForce : 1f);
        }

        /// <summary>Top speed on a slope: uphill costs speed (less with a high Crash rating), downhill adds it (0x476f90).</summary>
        float SpeedLimit(float vmax, bool boosting)
        {
            float f = (bodyPitch <= 0f ? 1f : 1f - UphillSpeedLoss) * bodyPitch;
            if (boosting && bodyPitch < 0f) f *= 0.5f;
            return ((gear > 0 ? -1f : 1f) * f + 1f) * vmax;
        }

        /// <summary>Body roll from the lateral g and pitch from the longitudinal one, a couple of degrees at most.</summary>
        void BodyTilt(float dt, float speed, bool boosting)
        {
            float roll = speed / turnRadius * speed / G / 1.28f * CgHeight * Mass / (Mass * 0.5f);
            if (state == 2) roll /= DriftSteerGain;
            roll = Mathf.Clamp(roll, -12f, 12f);
            if (state == 0) rollF.Set(rollMass, 15f, 50f / rollMass);
            else { roll *= boosting ? 2f : 1.5f; rollF.Set(rollMass, 10f, 50f / rollMass); }
            rollF.Step(roll, dt);
            float transfer = longForce / Mass / G / WheelBase * CgHeight * Mass;
            float pitch = state == 1 ? 2.5f : Mathf.Clamp(-transfer / 50f, -3.5f, 3.5f);
            pitchF.Set(1f, 5f, state == 1 ? 10f : 20f);
            pitchF.Step(pitch, dt);
        }

        Vector2 MoveDir(float speed) => speed >= 0.1f ? vel / speed : (gear >= 1 ? Dir(heading) : -Dir(heading));
        float LateralSpeed() => Vector2.Dot(vel, Right(Dir(heading)));
        float ForwardSpeed() => Vector2.Dot(vel, Dir(heading));
        static Vector2 Dir(float yaw) => new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
        static Vector2 Right(Vector2 d) => new Vector2(d.y, -d.x);
        static float WrapPi(float a) => Mathf.Repeat(a + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

        void OnCollisionEnter(Collision c)
        {
            if (c.rigidbody && !c.rigidbody.isKinematic) hitObjectEnter = true;
            NoteHit(c);
        }
        void OnCollisionStay(Collision c) => NoteHit(c);

        void NoteHit(Collision c)
        {
            Vector3 n = Vector3.zero;
            for (int i = 0; i < c.contactCount; i++)
            {
                var p = c.GetContact(i);
                if (p.normal.y > 0.6f) continue;   // the road under the chassis
                n += p.normal;
            }
            var h = new Vector2(n.x, n.z);
            if (h.sqrMagnitude < 1e-6f) return;
            if (!c.rigidbody || c.rigidbody.isKinematic) { hitWall = true; wallNormal += h.normalized; }
            else { hitObject = true; objectNormal += h.normalized; }
        }

        void Update()
        {
            // keys are sampled per frame; physics steps don't run every frame, so the press is kept until used
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.rKey.wasPressedThisFrame && !ExternalControl && !Net.ChatBox.Typing) resetRequested = true;
#else
            if (Input.GetKeyDown(KeyCode.R) && !ExternalControl && !Net.ChatBox.Typing) resetRequested = true;
#endif
            float dt = Time.deltaTime;
            // the front wheels show the steering through the client's 0.1 s critically damped smoothing
            steerVisual = SmoothCD(steerVisual, ref steerVisualVel, steerInput * MaxSteer, 0.1f, dt);
            float forward = body ? Vector3.Dot(body.linearVelocity, transform.forward) : 0f;
            float radius = Wheels[0] ? Mathf.Max(0.1f, Wheels[0].radius) : 0.33f;
            wheelSpin = Mathf.Repeat(wheelSpin + forward / radius * Mathf.Rad2Deg * dt, 360f);
            for (int i = 0; i < 4; i++)
            {
                if (!Wheels[i] || !WheelVisuals[i]) continue;
                Wheels[i].GetWorldPose(out var pos, out _);
                var rot = transform.rotation * Quaternion.Euler(0f, i < 2 ? steerVisual : 0f, 0f) * Quaternion.Euler(wheelSpin, 0f, 0f);
                WheelVisuals[i].SetPositionAndRotation(pos, rot * WheelVisualOffsets[i]);
            }
            if (!bodyVisual) { bodyVisual = transform.Find("Body"); if (bodyVisual) bodyBaseRot = bodyVisual.localRotation; }
            if (bodyVisual) bodyVisual.localRotation = Quaternion.Euler(pitchF.X, 0f, -rollF.X) * bodyBaseRot;
        }

        /// <summary>Game Programming Gems 4 SmoothCD, as the client smooths its steering visuals (0x476520).</summary>
        static float SmoothCD(float from, ref float velocity, float to, float smoothTime, float dt)
        {
            float omega = 2f / smoothTime, x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = from - to;
            float temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * exp;
            return to + (change + temp) * exp;
        }

        public void SetSpawn(Vector3 pos, Quaternion rot) { resetPos = pos; resetRot = rot; }

        void Respawn()
        {
            var spot = RespawnOverride?.Invoke();
            var pos = spot?.pos ?? resetPos;
            var rot = spot?.rot ?? resetRot;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(pos + Vector3.up * 0.5f, rot);
            body.position = transform.position; body.rotation = rot;
            vel = Vector2.zero; gear = 1; rpm = 0f; airState = 0; airTime = driftTime = 0f;
            boostTimer = boostDelay = 0f;
            miniBoost = stopTimer = postDriftWindow = 0f;
            synced = false;
        }

        /// <summary>Throttle, steering and drift from the bound keys / pad (Game/Options/KeyBindings.cs, goption.ini [Controller] / [Keyboard]).</summary>
        static void ReadInput(out float throttle, out float steer, out bool drift) => Game.KeyBindings.ReadDriving(out throttle, out steer, out drift, out _);

        /// <summary>
        /// vehicle\levelmodifier.dat ("DPDB"): for each rating level 0..1000 a row of parameters, one section per rating
        /// (Speed 2000-2002, Accel 3000-3003, Crash 4000-4018, Boost 5000-5002). Levels are clamped to 1..999 like the
        /// client. Read from the mounted install; the 50-level samples below stand in when it isn't mounted.
        /// </summary>
        static class LevelTable
        {
            static byte[] data;
            static object triedFiles;
            static int[] ids, idSection, idColumn, sectionStart, sectionWidth;

            static readonly int[] SampleIds = { 2000, 2001, 2002, 3000, 3001, 3002, 3003, 4000, 4005, 4012, 4014, 5000, 5001, 5002 };
            // levels 0, 50, ... 950, 999
            static readonly float[][] Samples =
            {
                new[] { 236f, 253.3f, 269.8f, 285.6f, 300.7f, 315f, 328.5f, 341.3f, 353.2f, 364.3f, 374.6f, 384f, 392.5f, 400.2f, 407f, 412.8f, 418.3f, 423.8f, 429.3f, 434.8f, 440.2f },
                new[] { 336f, 352.9f, 369.2f, 384.9f, 399.8f, 414f, 427.5f, 440.2f, 452.2f, 463.3f, 473.7f, 483.2f, 491.8f, 499.6f, 506.5f, 512.5f, 518f, 523.5f, 529f, 534.5f, 539.9f },
                new[] { 100f, 110.4f, 120.1f, 129.1f, 137.4f, 145.1f, 152.1f, 158.5f, 161f, 161f, 161f, 161f, 161f, 161f, 161f, 161f, 161f, 161f, 161f, 161f, 161f },
                new[] { 191.9f, 198.2f, 209.4f, 225.8f, 247.4f, 274f, 305.3f, 340.5f, 378.7f, 418.7f, 459.2f, 498.5f, 534.7f, 565.7f, 589f, 602.2f, 607.3f, 607.3f, 607.3f, 607.3f, 607.3f },
                new[] { 4867f, 3511f, 2516f, 1803f, 1292f, 925.5f, 663.1f, 475.2f, 340.5f, 244f, 174.8f, 125.3f, 89.75f, 64.31f, 46.08f, 33.02f, 23.66f, 23.66f, 23.66f, 23.66f, 23.66f },
                new[] { 1990f, 1550f, 1207f, 940f, 732.1f, 570.2f, 444f, 345.8f, 269.3f, 209.7f, 163.4f, 127.2f, 99.08f, 77.16f, 60.09f, 46.8f, 36.45f, 36.45f, 36.45f, 36.45f, 36.45f },
                new[] { 0.8836f, 0.8988f, 0.9167f, 0.9379f, 0.9629f, 0.9924f, 1.027f, 1.068f, 1.08f, 1.092f, 1.103f, 1.114f, 1.125f, 1.137f, 1.148f, 1.159f, 1.17f, 1.17f, 1.17f, 1.17f, 1.17f },
                new[] { 99.7f, 86.08f, 75.12f, 66.31f, 59.21f, 53.5f, 48f, 43f, 38f, 33f, 28f, 23f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f },
                new[] { 75.88f, 70.6f, 66.48f, 63.28f, 60.79f, 58.84f, 57.33f, 56.15f, 55.23f, 54.52f, 53.96f, 53.53f, 53.19f, 52.93f, 52.72f, 52.56f, 50f, 50f, 50f, 50f, 50f },
                new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.0025f, 0.1275f, 0.2525f, 0.3775f, 0.5025f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f },
                new[] { 100f, 109.4f, 118.8f, 128.1f, 137.5f, 146.9f, 156.2f, 165.6f, 175f, 184.4f, 193.8f, 203.1f, 212.5f, 221.9f, 231.2f, 240.6f, 250f, 250f, 250f, 250f, 250f },
                new[] { 2.85f, 3.35f, 3.849f, 4.349f, 4.848f, 5.348f, 5.847f, 6.347f, 6.846f, 7.346f, 7.845f, 8.345f, 8.844f, 9.344f, 9.843f, 10.34f, 10.84f, 11.34f, 11.84f, 12.34f, 12.83f },
                new[] { 2.95f, 3.005f, 3.06f, 3.115f, 3.17f, 3.225f, 3.28f, 3.335f, 3.39f, 3.445f, 3.499f, 3.554f, 3.609f, 3.664f, 3.719f, 3.774f, 3.829f, 3.884f, 3.939f, 3.994f, 4.048f },
                new[] { 70f, 65.81f, 62.04f, 58.66f, 55.66f, 53.01f, 50.68f, 48.66f, 46.92f, 45.45f, 44.21f, 43.19f, 42.36f, 41.7f, 41.19f, 40.81f, 40.53f, 40.33f, 40.19f, 40.09f, 40f },
            };

            public static float Get(int id, int level)
            {
                level = Mathf.Clamp(level, 1, 999);
                if (TryLoad())
                {
                    int i = System.Array.IndexOf(ids, id);
                    if (i >= 0)
                    {
                        int s = idSection[i];
                        int at = sectionStart[s] + (level * sectionWidth[s] + idColumn[i]) * 4;
                        if (at + 4 <= data.Length) return System.BitConverter.ToSingle(data, at);
                    }
                }
                int row = System.Array.IndexOf(SampleIds, id);
                if (row < 0) return 0f;
                int k = Mathf.Min(level / 50, 19);
                int a = k * 50, b = k == 19 ? 999 : a + 50;
                return Mathf.Lerp(Samples[row][k], Samples[row][k + 1], (level - a) / (float)(b - a));
            }

            static bool TryLoad()
            {
                if (data != null) return true;
                var fs = DriftCityData.Files;
                if (fs == null || ReferenceEquals(fs, triedFiles)) return false;
                triedFiles = fs;
                if (!fs.TryRead(@"vehicle\levelmodifier.dat", out var bytes)) return false;
                if (bytes.Length < 24 || bytes[0] != 'D' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'B') return false;
                var counts = new int[4];
                int total = 0;
                for (int s = 0; s < 4; s++) { counts[s] = System.BitConverter.ToInt32(bytes, 8 + s * 4); total += counts[s]; }
                if (total <= 0 || total > 256 || bytes.Length < 24 + total * 4) return false;
                ids = new int[total]; idSection = new int[total]; idColumn = new int[total];
                sectionStart = new int[4]; sectionWidth = new int[4];
                int offset = 24 + total * 4, n = 0;
                for (int s = 0; s < 4; s++)
                {
                    sectionStart[s] = offset; sectionWidth[s] = counts[s];
                    for (int c = 0; c < counts[s]; c++, n++)
                    {
                        ids[n] = System.BitConverter.ToInt32(bytes, 24 + n * 4);
                        idSection[n] = s; idColumn[n] = c;
                    }
                    offset += counts[s] * 4 * 1001;
                }
                data = bytes;
                return true;
            }
        }
    }
}
