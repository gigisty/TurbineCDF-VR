using System.Runtime.CompilerServices;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Keeps BrazeFX's transient object keys compatible across Unity's Instance ID to Entity ID transition.
    /// These values are intentionally used only for in-memory lookup, invalidation, and deterministic session seeds.
    /// </summary>
    internal static class BrazeObjectIdentity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int BrazeInstanceId(this Object value)
        {
            if (value == null) return 0;
#if UNITY_6000_3_OR_NEWER
            // Unity's EntityId hash is the supported 32-bit compatibility identity. In 6000.5 it is also
            // exactly the low 32 bits returned by the now-obsolete GetInstanceID implementation.
            return value.GetEntityId().GetHashCode();
#else
            return value.GetInstanceID();
#endif
        }
    }
}
