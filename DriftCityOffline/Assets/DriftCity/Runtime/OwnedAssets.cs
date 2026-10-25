using System.Collections.Generic;
using UnityEngine;

namespace DriftCity
{
    /// <summary>
    /// Meshes, materials, textures and render targets created for one built object (a NIF, a car, a city):
    /// destroyed together with it, so rebuilding cars and loading cities doesn't pile up GPU memory.
    /// </summary>
    public sealed class OwnedAssets : MonoBehaviour
    {
        readonly List<Object> assets = new List<Object>();

        /// <summary>Registers an asset to be destroyed with <paramref name="owner"/>; returns the asset.</summary>
        public static T Track<T>(GameObject owner, T asset) where T : Object
        {
            if (!owner || !asset) return asset;
            if (!owner.TryGetComponent<OwnedAssets>(out var o)) o = owner.AddComponent<OwnedAssets>();
            o.assets.Add(asset);
            return asset;
        }

        /// <summary>Destroys one tracked asset early (e.g. a paint copy being replaced).</summary>
        public static void Release(GameObject owner, Object asset)
        {
            if (!asset) return;
            if (owner && owner.TryGetComponent<OwnedAssets>(out var o)) o.assets.Remove(asset);
            if (asset is RenderTexture rt) rt.Release();
            Destroy(asset);
        }

        void OnDestroy()
        {
            foreach (var a in assets)
            {
                if (!a) continue;
                if (a is RenderTexture rt) rt.Release();
                Destroy(a);
            }
            assets.Clear();
        }
    }
}
