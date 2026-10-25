using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Real Match look on a car the remake does not rebuild for the race: the player's own car and remote players'
    /// cars (DriftCity_RE/PVP.md 14.4). In a Real Match room every driver races the RM car (the Duel); packet 0x1c7
    /// (0x6e7070) gives the character the room's RM car and 0x70e240 copies it into the 16 visual.xlt looks, so each
    /// slot's car is the Duel in that slot's colour, decal, decal colour and neon.
    ///
    /// The remake's player car is built once with its physics, so instead of swapping the body this component lays a
    /// Duel visual (CarFactory.BuildVisual + CarPaint.ApplyRealMatch) over the car and hides the car's own meshes
    /// (Renderer.forceRenderingOff) until <see cref="Remove"/>. The skin's wheels follow the car's: the local car's
    /// wheel visuals (spin, steering, suspension) and, on a remote car, a spin from its network speed.
    /// The skin's own lamps (its _light.nif: tail_light on braking, reversing_light backwards) and booster flames follow the
    /// car under it; the hidden car's booster flames (and the particle part they carry) are switched off meanwhile, so no
    /// flame burns at the hidden body's exhausts (PVP.md 15.4).
    /// </summary>
    public sealed class RealMatchSkin : MonoBehaviour
    {
        public int Slot { get; private set; }
        public GameObject Skin => skin;

        GameObject skin;
        readonly List<Renderer> hidden = new List<Renderer>();
        readonly List<Light> dimmed = new List<Light>();
        Transform[] skinTires;
        Vector3[] skinLocal;
        Quaternion[] skinOffset;
        Vector3[] carRest;
        float radius = CarFactory.DefaultTireRadius, spin;
        DriftCar car;
        Net.RemoteCar remote;
        Rigidbody body;
        GameObject skinTail, skinReverse;
        BoosterFlames skinFlames, hostFlames;
        bool lampBrake, lampReverse, flamesOn, hostFlamesWereOn;
        float lastSpeed;

        /// <summary>Tests: the skin's tail / reversing lamps and whether its booster flames burn.</summary>
        public bool BrakeLampOn => skinTail && skinTail.activeSelf;
        public bool ReverseLampOn => skinReverse && skinReverse.activeSelf;
        public bool FlamesOn => flamesOn;
        public BoosterFlames SkinFlames => skinFlames;
        public BoosterFlames HostFlames => hostFlames;

        /// <summary>Lays the Real Match car with slot <paramref name="slot"/>'s look over <paramref name="car"/> (no-op
        /// when it already wears that slot). Null when the RM car is not in the data.</summary>
        public static RealMatchSkin Apply(GameObject car, int slot)
        {
            if (!car) return null;
            var duel = Pvp.RealMatchCar();
            if (duel == null) return null;
            var existing = car.GetComponent<RealMatchSkin>();
            if (existing && existing.skin && existing.Slot == slot) return existing;
            if (existing) existing.Restore();
            var s = existing ? existing : car.AddComponent<RealMatchSkin>();
            s.Build(duel, slot);
            return s;
        }

        /// <summary>Takes the Real Match look off again (the car's own meshes come back).</summary>
        public static void Remove(GameObject car)
        {
            if (!car) return;
            var s = car.GetComponent<RealMatchSkin>();
            if (!s) return;
            s.Restore();
            Destroy(s);
        }

        void Build(VehicleInfo duel, int slot)
        {
            Slot = slot;
            car = GetComponent<DriftCar>();
            remote = GetComponent<Net.RemoteCar>();
            body = GetComponent<Rigidbody>();
            // the car's own meshes and lights go dark; particles (smoke, flames' sparks) and UI stay
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.forceRenderingOff || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                r.forceRenderingOff = true;
                hidden.Add(r);
            }
            foreach (var l in GetComponentsInChildren<Light>(true))
                if (l.enabled) { l.enabled = false; dimmed.Add(l); }

            var root = transform;
            skin = CarFactory.BuildVisual(duel, null, root.position, root.rotation, out _, out var tires);
            skin.name = "Real Match Skin";
            skin.transform.SetParent(root, true);
            skin.transform.localPosition = Vector3.zero;
            skin.transform.localRotation = Quaternion.identity;
            skin.transform.localScale = Vector3.one;
            CarPaint.ApplyRealMatch(skin, duel, Pvp.RealMatchLookFor(slot));
            foreach (var t in skin.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;
            // the skin is only a picture: no shadows of the hidden car's shape are lost, but keep its own colliders out
            foreach (var c in skin.GetComponentsInChildren<Collider>(true)) c.enabled = false;

            // the skin's lamps and flames are driven from here (their own components look for a DriftCar on the skin)
            var lights = skin.GetComponent<CarLights>();
            if (lights) lights.enabled = false;
            foreach (var t in skin.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "tail_light" && !skinTail) skinTail = t.gameObject;
                else if (t.name == "reversing_light" && !skinReverse) skinReverse = t.gameObject;
            }
            skinFlames = skin.GetComponent<BoosterFlames>();
            if (skinFlames) skinFlames.enabled = false;
            // the hidden car's flames would burn (with their particles) at its own exhausts: off while skinned
            hostFlames = GetComponent<BoosterFlames>();
            if (hostFlames)
            {
                hostFlamesWereOn = hostFlames.enabled;
                hostFlames.enabled = false;
                foreach (var f in hostFlames.Flames) if (f) f.SetActive(false);
            }
            lampBrake = lampReverse = flamesOn = false;

            radius = duel.TireRadius;
            skinTires = tires;
            skinLocal = new Vector3[4]; skinOffset = new Quaternion[4]; carRest = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                if (!tires[i]) continue;
                skinLocal[i] = root.InverseTransformPoint(tires[i].position);
                skinOffset[i] = Quaternion.Inverse(root.rotation) * tires[i].rotation;
                var w = car ? car.WheelVisuals[i] : null;
                carRest[i] = w ? root.InverseTransformPoint(w.position) : skinLocal[i];
            }
        }

        void LateUpdate()
        {
            if (!skin || skinTires == null) return;
            var root = transform;
            float speed = car ? (body ? body.linearVelocity.magnitude : 0f) : remote ? remote.SpeedKmh / 3.6f : 0f;
            UpdateLampsAndFlames(speed);
            spin = (spin + speed / Mathf.Max(0.1f, radius) * Mathf.Rad2Deg * Time.deltaTime) % 360f;
            for (int i = 0; i < 4; i++)
            {
                var t = skinTires[i];
                if (!t) continue;
                var w = car ? car.WheelVisuals[i] : null;
                if (w)
                {
                    // the wheel collider's pose = the car's tire visual without its build offset (DriftCar.Update)
                    var wheel = w.rotation * Quaternion.Inverse(car.WheelVisualOffsets[i]);
                    float lift = Mathf.Clamp(root.InverseTransformPoint(w.position).y - carRest[i].y, -0.25f, 0.25f);
                    t.SetPositionAndRotation(root.TransformPoint(skinLocal[i] + Vector3.up * lift), wheel * skinOffset[i]);
                }
                else
                    t.SetPositionAndRotation(root.TransformPoint(skinLocal[i]), root.rotation * Quaternion.AngleAxis(spin, Vector3.right) * skinOffset[i]);
            }
        }

        void UpdateLampsAndFlames(float speed)
        {
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            // a remote car carries no brake flag: a clear slow-down (over 4 m/s^2) lights the lamps
            bool brake = car ? car.Braking : speed < lastSpeed - 4f * dt && speed > 1f;
            bool reverse = car && body && Vector3.Dot(body.linearVelocity, transform.forward) < -0.5f;
            lastSpeed = speed;
            if (brake != lampBrake) { lampBrake = brake; if (skinTail) skinTail.SetActive(brake); }
            if (reverse != lampReverse) { lampReverse = reverse; if (skinReverse) skinReverse.SetActive(reverse); }
            bool boost = car ? car.Boosting : remote && remote.Boosting;
            if (boost != flamesOn)
            {
                flamesOn = boost;
                if (skinFlames) foreach (var f in skinFlames.Flames) if (f) f.SetActive(boost);
            }
            if (hostFlames && hostFlames.enabled) { hostFlames.enabled = false; foreach (var f in hostFlames.Flames) if (f) f.SetActive(false); }
        }

        void Restore()
        {
            if (hostFlames) hostFlames.enabled = hostFlamesWereOn || hostFlames.enabled;
            hostFlames = null; skinFlames = null; skinTail = skinReverse = null;
            foreach (var r in hidden) if (r) r.forceRenderingOff = false;
            hidden.Clear();
            foreach (var l in dimmed) if (l) l.enabled = true;
            dimmed.Clear();
            if (skin) Destroy(skin);
            skin = null;
            skinTires = null;
        }

        void OnDestroy() => Restore();
    }
}
