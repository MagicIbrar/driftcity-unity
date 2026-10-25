using System.Collections.Generic;
using DriftCity.Core.Nif;
using UnityEngine;

namespace DriftCity
{
    /// <summary>
    /// Plays a built NIF's keyframe controllers, as Gamebryo does every frame: NiTransformController (rotation,
    /// translation and scale keys on a node), NiVisController (show/hide), NiAlphaController (material alpha) and
    /// NiFlipController (texture flip-book). Keys are played linearly; each controller keeps its own frequency,
    /// phase, start/stop range and cycle type (loop, reverse, clamp).
    /// </summary>
    public sealed class NifAnimator : MonoBehaviour
    {
        public abstract class Channel
        {
            public float Frequency = 1f, Phase, Start, Stop;
            public int Cycle;   // 0 loop, 1 reverse (ping-pong), 2 clamp
            /// <summary>APP_INIT controllers start when shown; APP_TIME ones run on the shared game clock.</summary>
            public bool AppInit;
            /// <summary>Path of the animated object under the animator, to rebind copies made with Instantiate.</summary>
            public string Path;

            public Channel CloneFor(Transform root)
            {
                var c = (Channel)MemberwiseClone();
                var t = string.IsNullOrEmpty(Path) ? root : root.Find(Path);
                if (c is TransformChannel tc) tc.Target = t;
                else if (c is VisChannel vc) vc.Target = t ? t.gameObject : null;
                return c;
            }

            public void Init(NiTimeController c)
            {
                Frequency = c.Frequency; Phase = c.Phase; Start = c.StartTime; Stop = c.StopTime;
                Cycle = (c.Flags >> 1) & 3;
                AppInit = (c.Flags & 1) != 0;
            }

            public float LocalTime(float time)
            {
                float t = time * Frequency + Phase;
                float span = Stop - Start;
                if (span <= 1e-5f) return Start;
                switch (Cycle)
                {
                    case 1: return Start + Mathf.PingPong(t - Start, span);
                    case 2: return Mathf.Clamp(t, Start, Stop);
                    default: return Start + Mathf.Repeat(t - Start, span);
                }
            }

            public abstract void Apply(float t);
        }

        public sealed class TransformChannel : Channel
        {
            public Transform Target;
            public NiTransformData Data;

            public override void Apply(float t)
            {
                if (!Target) return;
                if (Data.Translations != null && Data.Translations.Count > 0)
                    Target.localPosition = DriftCityData.ToUnity(Sample(Data.Translations, t, (a, b, k) => new Vec3(Mathf.Lerp(a.X, b.X, k), Mathf.Lerp(a.Y, b.Y, k), Mathf.Lerp(a.Z, b.Z, k))));
                if (Data.Scales != null && Data.Scales.Count > 0)
                    Target.localScale = Vector3.one * Sample(Data.Scales, t, Mathf.Lerp);
                if (Data.Rotations.Count > 0)
                    Target.localRotation = DriftCityData.ToUnity(ToMatrix(Sample(Data.Rotations, t, Slerp)));
                else if (Data.Euler != null)
                {
                    float Angle(int axis) => Data.Euler[axis] != null && Data.Euler[axis].Count > 0 ? Sample(Data.Euler[axis], t, Mathf.Lerp) : 0f;
                    // Gamebryo's matrix for Euler keys (x, y, z) is the transpose of RotX*RotY*RotZ (checked against the
                    // static matrices of the intro's globe, rings, island card and logo, DriftCity_RE\INTRO.md)
                    Target.localRotation = DriftCityData.ToUnity(Transpose(Mul(Mul(RotX(Angle(0)), RotY(Angle(1))), RotZ(Angle(2)))));
                }
            }
        }

        public sealed class VisChannel : Channel
        {
            public GameObject Target;
            public KeyGroup<float> Keys;
            public override void Apply(float t)
            {
                bool on = SampleStep(Keys, t) > 0.5f;
                if (Target && Target.activeSelf != on) Target.SetActive(on);
            }
        }

        public sealed class AlphaChannel : Channel
        {
            public Material Material;
            public KeyGroup<float> Keys;
            public override void Apply(float t)
            {
                if (!Material) return;
                var c = Material.color; c.a = Mathf.Clamp01(Sample(Keys, t, Mathf.Lerp)); Material.color = c;
            }
        }

        public sealed class FlipChannel : Channel
        {
            public Material Material;
            public Texture2D[] Textures;
            public KeyGroup<float> Keys;
            public override void Apply(float t)
            {
                if (!Material || Textures.Length == 0) return;
                int i = Mathf.Clamp(Mathf.FloorToInt(Sample(Keys, t, Mathf.Lerp)), 0, Textures.Length - 1);
                if (Textures[i] && Material.mainTexture != Textures[i]) Material.mainTexture = Textures[i];
            }
        }

        public readonly List<Channel> Channels = new List<Channel>();
        /// <summary>Run every controller from the moment the object is shown (effects that restart, like a booster burst).</summary>
        public bool LocalClock;
        float time;

        // Instantiate copies serialized fields only: copies of a built NIF find their template's channels by this key
        static readonly Dictionary<int, NifAnimator> templates = new Dictionary<int, NifAnimator>();
        [SerializeField] int templateKey;

        public void Add(Channel c, Transform target)
        {
            c.Path = target == transform ? "" : RelativePath(target);
            if (templateKey == 0) { templateKey = GetInstanceID(); templates[templateKey] = this; }
            Channels.Add(c);
        }

        string RelativePath(Transform t)
        {
            var parts = new List<string>();
            for (; t && t != transform; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        void Awake()
        {
            if (Channels.Count == 0 && templateKey != 0 && templates.TryGetValue(templateKey, out var tpl) && tpl && tpl != this)
                foreach (var c in tpl.Channels) Channels.Add(c.CloneFor(transform));
        }

        void OnDestroy()
        {
            if (templateKey != 0 && templates.TryGetValue(templateKey, out var tpl) && tpl == this) templates.Remove(templateKey);
        }

        void OnEnable() { time = 0f; }

        /// <summary>Poses everything at a time (batch renders outside play mode).</summary>
        public void Sample(float t) { foreach (var c in Channels) c.Apply(c.LocalTime(t)); }

        void Update()
        {
            time += Time.deltaTime;
            foreach (var c in Channels) c.Apply(c.LocalTime(c.AppInit || LocalClock ? time : Time.time));
        }

        // ---------------- sampling ----------------
        static T Sample<T>(KeyGroup<T> g, float t, System.Func<T, T, float, T> lerp)
        {
            int n = g.Count;
            if (n == 1 || t <= g.Times[0]) return g.Values[0];
            if (t >= g.Times[n - 1]) return g.Values[n - 1];
            int i = 1;
            while (i < n - 1 && g.Times[i] < t) i++;
            float span = g.Times[i] - g.Times[i - 1];
            return lerp(g.Values[i - 1], g.Values[i], span > 1e-6f ? (t - g.Times[i - 1]) / span : 1f);
        }

        static float SampleStep(KeyGroup<float> g, float t)
        {
            if (g == null || g.Count == 0) return 1f;
            float v = g.Values[0];
            for (int i = 0; i < g.Count && g.Times[i] <= t; i++) v = g.Values[i];
            return v;
        }

        static Quat Slerp(Quat a, Quat b, float k)
        {
            var q = Quaternion.Slerp(new Quaternion(a.X, a.Y, a.Z, a.W), new Quaternion(b.X, b.Y, b.Z, b.W), k);
            return new Quat { W = q.w, X = q.x, Y = q.y, Z = q.z };
        }

        /// <summary>Gamebryo's NiQuaternion::ToRotation, in the same convention as the matrices NIFs store.</summary>
        static Mat3 ToMatrix(Quat q)
        {
            float w = q.W, x = q.X, y = q.Y, z = q.Z;
            return new Mat3
            {
                M00 = 1 - 2 * (y * y + z * z), M01 = 2 * (x * y + w * z), M02 = 2 * (x * z - w * y),
                M10 = 2 * (x * y - w * z), M11 = 1 - 2 * (x * x + z * z), M12 = 2 * (y * z + w * x),
                M20 = 2 * (x * z + w * y), M21 = 2 * (y * z - w * x), M22 = 1 - 2 * (x * x + y * y),
            };
        }

        static Mat3 Transpose(Mat3 m) => new Mat3 { M00 = m.M00, M01 = m.M10, M02 = m.M20, M10 = m.M01, M11 = m.M11, M12 = m.M21, M20 = m.M02, M21 = m.M12, M22 = m.M22 };
        static Mat3 RotX(float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Mat3 { M00 = 1, M11 = c, M12 = s, M21 = -s, M22 = c }; }
        static Mat3 RotY(float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Mat3 { M00 = c, M02 = -s, M11 = 1, M20 = s, M22 = c }; }
        static Mat3 RotZ(float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Mat3 { M00 = c, M01 = s, M10 = -s, M11 = c, M22 = 1 }; }

        static Mat3 Mul(Mat3 a, Mat3 b) => new Mat3
        {
            M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
        };
    }
}
