using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Deterministic flipbook-camera framing. View presets only choose orientation; projection remains a
    /// real perspective or orthographic camera and the selected bounds are fitted against the output cell aspect.</summary>
    internal static class BrazeFlipbookCameraFraming
    {
        internal const float PerspectiveFieldOfView = 35f;

        internal readonly struct Frame
        {
            public readonly Vector3 position;
            public readonly Quaternion rotation;
            public readonly bool orthographic;
            public readonly float fieldOfView;
            public readonly float orthographicSize;
            public readonly float nearClip;
            public readonly float farClip;

            internal Frame(Vector3 position, Quaternion rotation, bool orthographic, float fieldOfView,
                float orthographicSize, float nearClip, float farClip)
            {
                this.position = position;
                this.rotation = rotation;
                this.orthographic = orthographic;
                this.fieldOfView = fieldOfView;
                this.orthographicSize = orthographicSize;
                this.nearClip = nearClip;
                this.farClip = farClip;
            }

            internal void Apply(Camera camera)
            {
                camera.transform.SetPositionAndRotation(position, rotation);
                camera.orthographic = orthographic;
                camera.fieldOfView = fieldOfView;
                camera.orthographicSize = orthographicSize;
                camera.nearClipPlane = nearClip;
                camera.farClipPlane = farClip;
            }

            /// <summary>World-space width/height represented by the complete output cell on a plane through the
            /// capture centre. The generated Player uses this instead of the raw 3D domain size.</summary>
            internal Vector2 PlaneSizeAt(Vector3 center, float aspect)
            {
                aspect = Mathf.Max(0.01f, aspect);
                if (orthographic) return new Vector2(orthographicSize * 2f * aspect, orthographicSize * 2f);
                float depth = Mathf.Max(0.01f, Vector3.Dot(center - position, rotation * Vector3.forward));
                float height = 2f * depth * Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f);
                return new Vector2(height * aspect, height);
            }
        }

        internal static Frame Resolve(Vector3 sourcePosition, Quaternion sourceRotation, Vector3 localCenter,
            Vector3 localSize, BrazeFlipbookAsset.ViewDirection direction,
            BrazeExportSession.FlipbookProjection projection, float aspect, float padding)
        {
            aspect = Mathf.Max(0.01f, aspect);
            Vector3 center = sourcePosition + sourceRotation * localCenter;
            Vector3 localForward = LocalForward(direction);
            Vector3 localUp = direction == BrazeFlipbookAsset.ViewDirection.Top ? Vector3.forward
                : direction == BrazeFlipbookAsset.ViewDirection.Bottom ? Vector3.back : Vector3.up;
            Vector3 forward = sourceRotation * localForward;
            Vector3 up = sourceRotation * localUp;
            Quaternion cameraRotation = Quaternion.LookRotation(forward, up);
            Vector3 right = cameraRotation * Vector3.right;
            Vector3 cameraUp = cameraRotation * Vector3.up;

            float marginScale = 1f + Mathf.Clamp(padding, 0f, 0.5f);
            Vector3 half = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z)) *
                           (0.5f * marginScale);
            half.x = Mathf.Max(half.x, 0.005f);
            half.y = Mathf.Max(half.y, 0.005f);
            half.z = Mathf.Max(half.z, 0.005f);

            float halfWidth = 0f, halfHeight = 0f;
            float minForward = float.MaxValue, maxForward = float.MinValue;
            float fov = PerspectiveFieldOfView;
            float verticalTan = Mathf.Tan(fov * Mathf.Deg2Rad * 0.5f);
            float horizontalTan = verticalTan * aspect;
            float perspectiveDistance = float.MinValue;
            for (int z = -1; z <= 1; z += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int x = -1; x <= 1; x += 2)
            {
                Vector3 relative = sourceRotation * new Vector3(half.x * x, half.y * y, half.z * z);
                float across = Mathf.Abs(Vector3.Dot(relative, right));
                float vertical = Mathf.Abs(Vector3.Dot(relative, cameraUp));
                halfWidth = Mathf.Max(halfWidth, across);
                halfHeight = Mathf.Max(halfHeight, vertical);
                float along = Vector3.Dot(relative, forward);
                minForward = Mathf.Min(minForward, along);
                maxForward = Mathf.Max(maxForward, along);
                perspectiveDistance = Mathf.Max(perspectiveDistance,
                    across / Mathf.Max(horizontalTan, 1e-4f) - along);
                perspectiveDistance = Mathf.Max(perspectiveDistance,
                    vertical / Mathf.Max(verticalTan, 1e-4f) - along);
            }

            bool orthographic = projection == BrazeExportSession.FlipbookProjection.Orthographic;
            float orthographicSize = Mathf.Max(0.01f, Mathf.Max(halfHeight, halfWidth / aspect));
            float extent = Mathf.Max(half.x, Mathf.Max(half.y, half.z));
            float clipMargin = Mathf.Max(0.01f, extent * 0.02f);
            float distance;

            if (orthographic)
            {
                distance = -minForward + clipMargin + 0.01f;
            }
            else
            {
                // Fit every corner against both frustum axes. This is exact for the chosen AABB/view, including
                // isometric and rectangular-cell captures where independent width/depth maxima over-frame badly.
                distance = Mathf.Max(-minForward + clipMargin + 0.01f, perspectiveDistance);
                distance += Mathf.Max(0.001f, extent * 0.001f);
            }

            float near = Mathf.Max(0.01f, distance + minForward - clipMargin);
            float far = Mathf.Max(near + 0.02f, distance + maxForward + clipMargin);
            return new Frame(center - forward * distance, cameraRotation, orthographic, fov,
                orthographicSize, near, far);
        }

        internal static Vector3 LocalForward(BrazeFlipbookAsset.ViewDirection direction) => direction switch
        {
            BrazeFlipbookAsset.ViewDirection.Back => Vector3.back,
            BrazeFlipbookAsset.ViewDirection.Left => Vector3.right,
            BrazeFlipbookAsset.ViewDirection.Right => Vector3.left,
            BrazeFlipbookAsset.ViewDirection.Top => Vector3.down,
            BrazeFlipbookAsset.ViewDirection.Bottom => Vector3.up,
            BrazeFlipbookAsset.ViewDirection.Isometric => new Vector3(1f, -0.75f, 1f).normalized,
            _ => Vector3.forward,
        };
    }
}
