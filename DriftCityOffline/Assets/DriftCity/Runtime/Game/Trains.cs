using System.Collections.Generic;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Taipei MRT (DriftCity_RE/TRAFFIC.md section 9). The client loads taipei\path\trainpath.chpath and
    /// trainpath.signal when it enters city 5 (0x4a9b40) and runs, per path (one per direction), three six-car trains on
    /// a timetable computed from the server clock (0x4a9c90): cruise at 100 km/h, brake over 300 m into each station,
    /// wait 8 s, accelerate out. Cars are vehiclelist 20505 (vehicle\traffic\c_t_5_mrt.nif) placed every 12 m back along
    /// the path, following its direction. A train is only shown while 2 x 6 x 12 m plus its distance to the camera stays
    /// within the view-distance option x 200 m (0x4a91c0). The cars are solid, unstoppable bodies.
    /// </summary>
    public sealed class Trains : MonoBehaviour
    {
        /// <summary>
        /// The client's view-distance option (DAT_008d297c = goption.ini [Graphic] VisibleRange, read clamped to 1..5);
        /// trains show within option x 200 m, pedestrians within 100 + 15 x option. Reads GameOptions.VisibleRange unless a
        /// test sets it (set 0 to go back to the option).
        /// </summary>
        public static int ViewOption
        {
            get => viewOverride > 0 ? viewOverride : GameOptions.VisibleRange;
            set => viewOverride = value;
        }
        static int viewOverride;

        public readonly List<TrainSchedule> Lines = new List<TrainSchedule>();
        public Transform Player;
        GameObject carTemplate;
        Vector3 carHalf = new Vector3(1.2f, 1.5f, 7.6f);
        readonly List<Train> trains = new List<Train>();

        public sealed class Train
        {
            public int Line, Index;
            public GameObject Root;
            public Rigidbody[] Cars;
            public float Position, Speed;
            public bool Stopped, Visible;
        }

        public IReadOnlyList<Train> All => trains;

        /// <summary>Starts the trains of <paramref name="city"/> (only Taipei has a train path). Returns null elsewhere.</summary>
        public static Trains Attach(CitySession session, string city) => Attach(city, session ? session.Car.transform : null, session ? session.transform : null);

        public static Trains Attach(string city, Transform player, Transform parent)
        {
            var fs = DriftCityData.Files;
            string chpath = $@"{city}\path\trainpath.chpath", signal = $@"{city}\path\trainpath.signal";
            if (fs == null || !fs.Exists(chpath)) return null;
            var paths = ChPathFile.Parse(fs.Read(chpath));
            var signals = fs.Exists(signal) ? SignalFile.Parse(fs.Read(signal)) : new List<List<PathSignal>>();
            if (paths.Count == 0) return null;
            var go = new GameObject("Trains");
            if (parent) go.transform.SetParent(parent, false);
            var t = go.AddComponent<Trains>();
            t.Player = player;
            for (int i = 0; i < paths.Count; i++)
                t.Lines.Add(new TrainSchedule(paths[i], i < signals.Count ? signals[i] : null));
            t.Build();
            Debug.Log($"Drift City: {paths.Count} train paths in {city}, {t.trains.Count} trains");
            return t;
        }

        void Build()
        {
            string nif = @"vehicle\traffic\c_t_5_mrt.nif";
            var holder = new GameObject("Train Template");
            holder.transform.SetParent(transform, false);
            holder.SetActive(false);
            try
            {
                carTemplate = new NifSceneBuilder { LogErrors = false }.Load(nif, holder.transform);
                var rs = carTemplate.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                    carHalf = b.extents;
                    carCentre = carTemplate.transform.InverseTransformPoint(b.center);
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("train model: " + ex.Message); }

            for (int line = 0; line < Lines.Count; line++)
                for (int k = 0; k < TrainSchedule.TrainsPerPath; k++)
                {
                    var tr = new Train { Line = line, Index = k, Root = new GameObject($"Train {line}.{k}"), Cars = new Rigidbody[TrainSchedule.CarsPerTrain] };
                    tr.Root.transform.SetParent(transform, false);
                    for (int c = 0; c < tr.Cars.Length; c++)
                    {
                        var car = carTemplate ? Instantiate(carTemplate, tr.Root.transform) : new GameObject();
                        car.name = $"MRT car {c}";
                        car.transform.SetParent(tr.Root.transform, false);
                        car.SetActive(true);
                        var rb = car.AddComponent<Rigidbody>();
                        rb.isKinematic = true;
                        rb.interpolation = RigidbodyInterpolation.Interpolate;
                        rb.mass = 30000f;
                        var box = car.AddComponent<BoxCollider>();
                        box.center = carCentre;
                        box.size = carHalf * 2f;
                        tr.Cars[c] = rb;
                    }
                    tr.Root.SetActive(false);
                    trains.Add(tr);
                }
        }

        Vector3 carCentre;

        /// <summary>Game clock the timetables run on: the client takes the server's millisecond clock modulo 10 hours
        /// (0x4a9c90, 0x49b6e0); UTC milliseconds give every player the same trains and pedestrians.</summary>
        public static float Clock => (float)(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 36000000L / 1000.0);
        /// <summary>Overrides the clock (tests).</summary>
        public static float? ClockOverride;
        public static float Now => ClockOverride ?? Clock;

        void FixedUpdate()
        {
            float time = Now;
            var cam = Camera.main ? Camera.main.transform : Player;
            float range = ViewOption * 200f;
            foreach (var tr in trains)
            {
                var line = Lines[tr.Line];
                tr.Position = line.Position(tr.Index, time, out tr.Speed, out tr.Stopped);
                var head = DriftCityData.ToUnity(line.Path.Sample(tr.Position, out _));
                float dist = cam ? Vector3.Distance(cam.position, head) : 0f;
                bool show = 2f * TrainSchedule.CarsPerTrain * TrainSchedule.CarSpacing + dist <= range;
                if (show != tr.Visible)
                {
                    tr.Visible = show;
                    tr.Root.SetActive(show);
                    if (show) Place(tr, line, true);
                }
                if (show) Place(tr, line, false);
            }
        }

        void Place(Train tr, TrainSchedule line, bool snap)
        {
            for (int c = 0; c < tr.Cars.Length; c++)
            {
                float t = tr.Position - c * TrainSchedule.CarSpacing * 0.036f;
                // the cars behind the first wrap with it to the far end of the path at the start of a cycle
                if (t < 0) t = 0;
                var p = DriftCityData.ToUnity(line.Path.Sample(t, out var d));
                var fwd = DriftCityData.ToUnity(d);
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                var rb = tr.Cars[c];
                if (snap) { rb.transform.SetPositionAndRotation(p, rot); rb.position = p; rb.rotation = rot; }
                else { rb.MovePosition(p); rb.MoveRotation(rot); }
            }
        }
    }
}
