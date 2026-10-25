using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The car's lamps from &lt;model&gt;_light.nif. The client looks up head_light, tail_light, reversing_light,
    /// winker_left/right, warning_light, high_light and Siren on that model (DriftCity_RE/decompiled/phys/0x45dc60.c)
    /// and switches them with the car's state; the lamp materials are authored fully transparent and only show
    /// while lit. Here the brake lights follow braking and the reversing light follows driving backwards.
    /// </summary>
    public sealed class CarLights : MonoBehaviour
    {
        static readonly string[] Names = { "head_light", "tail_light", "reversing_light", "winker_left", "winker_right", "warning_light", "high_light", "Siren" };
        readonly Dictionary<string, GameObject> lamps = new Dictionary<string, GameObject>();
        DriftCar car;
        Rigidbody rb;
        bool brake, reverse;

        public static void Attach(GameObject root, string nif)
        {
            if (!DriftCityData.Files.Exists(nif)) return;
            var model = new NifSceneBuilder { LogErrors = false }.Load(nif, root.transform);
            model.name = "Lights";
            var lights = root.AddComponent<CarLights>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (System.Array.IndexOf(Names, t.name) < 0 || lights.lamps.ContainsKey(t.name)) continue;
                lights.lamps[t.name] = t.gameObject;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    // this car's own copy, lit at full opacity
                    var m = OwnedAssets.Track(root, new Material(r.sharedMaterial));
                    var c = m.color; c.a = 1f; m.color = c;
                    r.sharedMaterial = m;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                t.gameObject.SetActive(false);
            }
        }

        void Start() { car = GetComponent<DriftCar>(); rb = GetComponent<Rigidbody>(); }

        void Update()
        {
            if (!car) return;
            bool b = car.Braking;
            bool r = rb && Vector3.Dot(rb.linearVelocity, transform.forward) < -0.5f;
            if (b != brake) { brake = b; Set("tail_light", b); }
            if (r != reverse) { reverse = r; Set("reversing_light", r); }
        }

        void Set(string name, bool on) { if (lamps.TryGetValue(name, out var go) && go) go.SetActive(on); }
    }
}
