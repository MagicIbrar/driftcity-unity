using UnityEngine;

namespace DriftCity
{
    /// <summary>
    /// The client's "outrun" driving cameras (camera.ini normalview1 / normalview2, computed inside the car update
    /// 0x4809c0 from the presets of 0x4793b0): Distance behind and Height above the car, aimed at the car LookHeight up
    /// (plus sin(pitch) * LookAhead on slopes). It swings after the car's heading on a stiff spring kept within 45 degrees,
    /// eases back up to 2 m with speed, follows the slope, widens while boosting and is pushed out of walls (the client
    /// collides a 0.3 m sphere). The view comes from goption.ini [Game] DefaultPlayerCamera (0 front, 1 normalview1,
    /// 2 normalview2, 3 bonnet; 0x4dc960), 2 in the shipped file: the high view the client starts with.
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        public Transform Target;
        public float Distance = 8f;        // "N"
        public float Height = 3f;          // "H"
        public float LookAhead = 10f;      // "F"
        public float LookHeight = 1f;      // 4th preset value (inferred: x1.5 with H for unique id 0x53, matches the client's 14 degree pitch)
        public float SpeedPullBack = 2f;
        /// <summary>Horizontal half-angles of the client's 4:3 view, normal and boosting (vertical FOV about 47 and 74 degrees).</summary>
        public float FovHalf = 30f, BoostFovHalf = 45f;

        /// <summary>The client's gameplay views: 1 = normalview1 (low, 5.5 m behind), 2 = normalview2 (high, 8 m behind).</summary>
        public int View { get => view; set { view = value == 1 ? 1 : 2; viewChosen = true; ApplyView(); SaveView(); } }
        int view = 2;
        bool viewChosen;

        /// <summary>Presets of 0x4793b0: F, N, H, look height, FOV and boost FOV (horizontal half-angles).</summary>
        void ApplyView()
        {
            if (view == 1) { LookAhead = 30f; Distance = 5.5f; Height = 1.85f; LookHeight = 1f; }
            else { LookAhead = 10f; Distance = 8f; Height = 3f; LookHeight = 1f; }
            FovHalf = 30f; BoostFovHalf = 45f;
        }

        /// <summary>DefaultPlayerCamera from goption.ini (Game.GameOptions; the shipped value is 2). Front (0) and bonnet (3)
        /// views aren't rebuilt: the high view stands in.</summary>
        /// <summary>The client saves the camera in use as the next default.</summary>
        void SaveView() { if (Game.GameOptions.DefaultPlayerCamera == view) return; Game.GameOptions.DefaultPlayerCamera = view; Game.GameOptions.Save(); }

        public static int DefaultView() => Game.GameOptions.DefaultPlayerCamera == 1 ? 1 : 2;


        /// <summary>The custom shop's view of the parked car (init\vshop_camera.ini presets).</summary>
        public bool Showroom;
        /// <summary>vshop_camera.ini preset: yaw (radians round the car: pi/2 behind, pi left, 3pi/2 in front), pitch (negative = from above), zoom offset.</summary>
        public static (float yaw, float pitch, float zoom) ShowroomView = (3.9f, -0.3f, -0.3f);

        Transform bound;
        DriftCar car;
        Rigidbody rb;
        Camera cam;
        float yaw, yawVel, pitch, pitchVel, pull, pullVel, fov, fovVel, lift, liftVel;

        /// <summary>The jump lift (in[0x6ce]): metres the camera rises over a long jump, back to 0 on landing.</summary>
        public float JumpLift => lift;
        readonly RaycastHit[] hits = new RaycastHit[16];

        void LateUpdate()
        {
            if (!Target) return;
            if (Showroom)
            {
                bound = null;
                var (vy, vp, vz) = ShowroomView;
                // zoom shifts the distance from the car's centre, kept within the file's max_zoomin/max_zoomout
                float dist = Mathf.Clamp(5f * (1f + vz * 0.5f), 0.8f, 6f);
                var round = new Vector3(Mathf.Cos(vy), 0, -Mathf.Sin(vy)) * Mathf.Cos(-vp) * dist;
                var offset = Target.TransformDirection(round) + Vector3.up * (Mathf.Sin(-vp) * dist + 0.6f);
                transform.position = Vector3.Lerp(transform.position, Target.position + offset, 1 - Mathf.Exp(-4f * Time.unscaledDeltaTime));
                transform.LookAt(Target.position + Vector3.up * 0.6f);
                return;
            }
            if (Target != bound) Bind();

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            var forward = Target.forward;
            float heading = forward.x * forward.x + forward.z * forward.z > 1e-4f ? Mathf.Atan2(forward.x, forward.z) : yaw;
            float bodyPitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f));
            bool boosting = car && car.Boosting;
            bool airborne = car && car.Airborne;
            bool longJump = airborne && car.AirTime > 0.6f;
            float vmax = car ? Mathf.Max(1f, car.TopSpeedKmh / 3.6f) : 60f;
            float speed = rb ? new Vector2(rb.linearVelocity.x, rb.linearVelocity.z).magnitude : 0f;
            float lag = car ? Mathf.Max(0.001f, car.CameraLag) : 1f;

            float pullTarget = SpeedPullBack * Mathf.Min(speed / vmax, 1f) + (longJump ? 5f : 0f);
            float pm = 1f, pd = 10f, pk = 10f;
            if (longJump) { pd = 5f; pk = 2f; }
            else if (boosting) { pm = 0.25f; pk = 150f; }
            float pitchTarget = airborne ? Mathf.Min(bodyPitch, 0f) : bodyPitch;
            float fovTarget = boosting ? BoostFovHalf : FovHalf;
            int n = Mathf.Max(1, Mathf.CeilToInt(dt / 0.005f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                // yaw spring of the client: m = car lag (4 just after a drift starts), d = 50, k = 200 / m
                float err = Mathf.DeltaAngle(heading * Mathf.Rad2Deg, yaw * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                yawVel += (-err * (200f / lag) - 50f * yawVel) / lag * h;
                yaw += yawVel * h;
                Follow(ref pull, ref pullVel, pullTarget, pm, pd, pk, h);
                Follow(ref pitch, ref pitchVel, pitchTarget, 1f, 10f, 40f, h);
                Follow(ref fov, ref fovVel, fovTarget, 1f, 10f, 10f, h);
                // the jump lift (0x4809c0, in[0x6cb..0x6d0]): over a jump longer than 0.6 s the height follows
                // (H + CamH) / (N + CamN) x 22 on a slow (1, 5, 2) follower; otherwise it is zeroed at once
                if (longJump)
                {
                    float back0 = Distance + (car ? car.CamN : 0f);
                    if (Mathf.Abs(back0) < 0.1f) back0 = 0.1f;
                    Follow(ref lift, ref liftVel, (Height + (car ? car.CamH : 0f)) / back0 * 22f, 1f, 5f, 2f, h);
                }
                else lift = liftVel = 0f;
            }
            float off = Mathf.DeltaAngle(heading * Mathf.Rad2Deg, yaw * Mathf.Rad2Deg);
            if (Mathf.Abs(off) > 45f) yaw = heading + Mathf.Sign(off) * 45f * Mathf.Deg2Rad;

            var flat = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            var aim = flat * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
            var pivot = Target.position;
            float back = Distance + (car ? car.CamN : 0f) + pull;
            float up = Height + (car ? car.CamH : 0f) + lift;
            var desired = Unblock(pivot + Vector3.up * 1.2f, pivot - aim * back + Vector3.up * up);
            // the client aims at the car (LookHeight up); F only tilts the aim on slopes (sin(pitch) * F)
            var look = pivot + Vector3.up * (LookHeight + Mathf.Sin(pitch) * LookAhead);
            transform.position = desired;
            transform.rotation = Quaternion.LookRotation(look - desired, Vector3.up);
            // the arcade look runs its own speed zoom
            if (cam && !Game.ArcadeLook.Enabled)
                cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(fov * Mathf.Deg2Rad) * 0.75f) * Mathf.Rad2Deg;
        }

        void Bind()
        {
            // the game data is mounted by the time a car is followed: read the player's view setting once
            if (!viewChosen) { view = DefaultView(); viewChosen = true; ApplyView(); }
            bound = Target;
            car = Target.GetComponent<DriftCar>();
            rb = Target.GetComponent<Rigidbody>();
            cam = GetComponent<Camera>();
            var f = Target.forward;
            yaw = Mathf.Atan2(f.x, f.z);
            yawVel = pitch = pitchVel = pull = pullVel = fovVel = lift = liftVel = 0f;
            fov = FovHalf;
        }

        static void Follow(ref float x, ref float v, float target, float m, float d, float k, float dt)
        {
            v += (-(x - target) * k - d * v) / m * dt;
            x += v * dt;
        }

        /// <summary>Keeps the camera on the car's side of walls (static geometry only; traffic doesn't shove it).</summary>
        Vector3 Unblock(Vector3 from, Vector3 to)
        {
            var d = to - from;
            float len = d.magnitude;
            if (len < 0.01f) return to;
            d /= len;
            int count = Physics.RaycastNonAlloc(from, d, hits, len, ~0, QueryTriggerInteraction.Ignore);
            float best = len;
            for (int i = 0; i < count; i++)
            {
                var body = hits[i].collider.attachedRigidbody;
                if (body && (body == rb || !body.isKinematic)) continue;
                if (hits[i].distance < best) best = hits[i].distance;
            }
            return best < len ? from + d * Mathf.Max(0.3f, best - 0.3f) : to;
        }
    }
}
