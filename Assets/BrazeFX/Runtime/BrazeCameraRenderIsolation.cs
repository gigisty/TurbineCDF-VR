using System;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Scopes a camera render to one cache/live slice container. Export Studio uses this while its
    /// synchronous camera render is in flight so the regular URP renderer remains unchanged for every other camera.
    /// The camera's culling mask handles ordinary Unity renderers; this scope handles BrazeFX feature passes, which
    /// are submitted procedurally and therefore do not participate in Unity's normal layer cull.</summary>
    internal static class BrazeCameraRenderIsolation
    {
        static Camera activeCamera;
        static BrazeContainer activeContainer;
        static int activeToken;
        static int nextToken;

        internal readonly struct Scope : IDisposable
        {
            readonly Camera previousCamera;
            readonly BrazeContainer previousContainer;
            readonly int previousToken;
            readonly int token;

            internal Scope(Camera oldCamera, BrazeContainer oldContainer, int oldToken, int newToken)
            {
                previousCamera = oldCamera;
                previousContainer = oldContainer;
                previousToken = oldToken;
                token = newToken;
            }

            public void Dispose()
            {
                // Ignore a stale/out-of-order Dispose instead of tearing down a newer nested render scope.
                if (activeToken != token) return;
                activeCamera = previousCamera;
                activeContainer = previousContainer;
                activeToken = previousToken;
            }
        }

        internal static Scope Begin(Camera camera, BrazeContainer container)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (container == null) throw new ArgumentNullException(nameof(container));
            int token = unchecked(++nextToken);
            if (token == 0) token = unchecked(++nextToken);
            var scope = new Scope(activeCamera, activeContainer, activeToken, token);
            activeCamera = camera;
            activeContainer = container;
            activeToken = token;
            return scope;
        }

        internal static bool IsEffectOnly(Camera camera)
            => camera != null && activeCamera == camera && activeContainer != null;

        internal static bool TryGetContainer(Camera camera, out BrazeContainer container)
        {
            bool matches = IsEffectOnly(camera);
            container = matches ? activeContainer : null;
            return matches;
        }
    }
}
