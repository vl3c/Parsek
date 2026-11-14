using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Self-cleanup component: destroys dynamically created Materials when the
    /// GameObject is destroyed (prevents Material leak on fire-and-forget objects).
    /// </summary>
    internal class MaterialCleanup : MonoBehaviour
    {
        public Material material;
        void OnDestroy()
        {
            if (material != null)
                Destroy(material);
        }
    }
}
