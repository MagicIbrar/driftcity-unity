using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The player car's effect sounds from the original audio: tyre skid while sliding, brake squeal,
    /// booster ignition, and impacts (traffic, HUVs, walls) scaled by how hard the hit was.
    /// </summary>
    [RequireComponent(typeof(DriftCar))]
    public sealed class CarSounds : MonoBehaviour
    {
        DriftCar car;
        Rigidbody body;
        AudioSource skid, brake, oneShots;
        bool wasBoosting;
        float lastHit;

        void Start()
        {
            car = GetComponent<DriftCar>();
            body = GetComponent<Rigidbody>();
            skid = Loop("sfx/SID_DRIFT");
            brake = Loop("sfx/SID_BRAKE");
            oneShots = gameObject.AddComponent<AudioSource>();
            oneShots.spatialBlend = 0f; oneShots.playOnAwake = false;
        }

        AudioSource Loop(string clip)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.clip = AudioLibrary.Wav(clip);
            src.loop = true; src.spatialBlend = 0f; src.volume = 0f; src.playOnAwake = false;
            if (src.clip) src.Play();
            return src;
        }

        bool Grounded()
        {
            foreach (var w in car.Wheels) if (w && w.isGrounded) return true;
            return false;
        }

        void Update()
        {
            if (!car || !body) return;
            float vol = AudioLibrary.SfxVolume;
            float speed = car.SpeedKmh;
            bool grounded = Grounded();
            float slide = grounded && speed > 20f ? Mathf.InverseLerp(10f, 35f, car.SlipAngle) : 0f;
            if (car.Handbrake && grounded && speed > 25f) slide = Mathf.Max(slide, 0.6f);
            Fade(skid, slide * 0.7f * vol, 6f);
            if (skid) skid.pitch = Mathf.Lerp(0.9f, 1.15f, Mathf.InverseLerp(20f, 160f, speed));
            Fade(brake, car.Braking && grounded && speed > 30f ? 0.5f * vol : 0f, 8f);

            if (car.Boosting && !wasBoosting && oneShots) oneShots.PlayOneShot(AudioLibrary.Wav("sfx/SID_TURBO_USE"), 0.8f * vol);
            wasBoosting = car.Boosting;
        }

        static void Fade(AudioSource src, float target, float rate)
        {
            if (!src) return;
            src.volume = Mathf.MoveTowards(src.volume, target, rate * Time.deltaTime);
        }

        void OnCollisionEnter(Collision c)
        {
            float impact = c.relativeVelocity.magnitude;
            if (impact < 4f || Time.time - lastHit < 0.15f || !oneShots) return;
            lastHit = Time.time;
            int strength = impact > 18f ? 3 : impact > 9f ? 2 : 1;
            var traffic = c.collider.GetComponentInParent<TrafficCar>();
            string clip = traffic != null
                ? (traffic.name.StartsWith("HUV") ? $"sfx/SID_HIT_HUV{strength}" : $"sfx/SID_HIT_TRAFFIC{strength}")
                : strength == 3 ? "sfx/SID_HIT_BUILDING" : strength == 2 ? "sfx/SID_HIT_FRINGE_M" : "sfx/SID_HIT_FRINGE_S";
            oneShots.PlayOneShot(AudioLibrary.Wav(clip), Mathf.Clamp01(impact / 20f) * AudioLibrary.SfxVolume);
        }
    }
}
