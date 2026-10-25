using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>Forwards the player car's contacts to <see cref="DriftCombo"/>, which lives on the HUD object.</summary>
    public sealed class DriftComboContacts : MonoBehaviour
    {
        public event System.Action<Collision> Contact;

        void OnCollisionEnter(Collision c) => Contact?.Invoke(c);
        void OnCollisionStay(Collision c) => Contact?.Invoke(c);
    }
}
