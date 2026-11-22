using UnityEngine;

namespace Mini.MR
{
    /// <summary>
    /// Where Mini can stand. Character and placement code only use this interface, so they do not care
    /// whether a surface came from a manual virtual plane (Quest 2 today), Meta scene planes or spatial
    /// anchors (future providers).
    /// </summary>
    public interface ISpatialEnvironmentProvider
    {
        string Name { get; }

        /// <summary>True if the provider finds real surfaces on its own. The manual provider returns false.</summary>
        bool SupportsSurfaceDetection { get; }

        /// <summary>Height (world Y, metres) of the surface Mini currently stands on.</summary>
        float SurfaceHeight { get; }

        /// <summary>Intersect a ray with a usable surface. The pose's up axis is the surface normal.</summary>
        bool TryGetSurfacePose(Ray ray, out Pose pose);

        /// <summary>Snap a world position onto the surface (keeps X/Z, replaces Y).</summary>
        Vector3 ProjectToSurface(Vector3 worldPosition);

        /// <summary>Set the surface height directly, e.g. from a controller touching the table.</summary>
        void SetSurfaceHeight(float worldHeight);

        void AdjustSurfaceHeight(float delta);
    }

    /// <summary>
    /// Quest 2 fallback: a single horizontal virtual surface at a height the user chooses (floor by
    /// default). The user can set it by touching a real table with the controller. It does not detect
    /// anything by itself and does not claim to.
    /// </summary>
    [DisallowMultipleComponent]
    public class ManualSurfaceProvider : MonoBehaviour, ISpatialEnvironmentProvider
    {
        [SerializeField] private float surfaceHeight;
        [SerializeField] private float minHeight;
        [SerializeField] private float maxHeight = 1.6f;
        [SerializeField] private float maxRayDistance = 4f;

        public string Name { get { return "Manual surface"; } }
        public bool SupportsSurfaceDetection { get { return false; } }
        public float SurfaceHeight { get { return surfaceHeight; } }

        public bool TryGetSurfacePose(Ray ray, out Pose pose)
        {
            pose = default(Pose);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, surfaceHeight, 0f));

            // Only accept rays that come from above and point down at the surface.
            if (ray.origin.y <= surfaceHeight || ray.direction.y >= -0.02f) return false;

            float distance;
            if (!plane.Raycast(ray, out distance) || distance > maxRayDistance) return false;

            pose = new Pose(ray.GetPoint(distance), Quaternion.identity);
            return true;
        }

        public Vector3 ProjectToSurface(Vector3 worldPosition)
        {
            worldPosition.y = surfaceHeight;
            return worldPosition;
        }

        public void SetSurfaceHeight(float worldHeight)
        {
            surfaceHeight = Mathf.Clamp(worldHeight, minHeight, maxHeight);
        }

        public void AdjustSurfaceHeight(float delta)
        {
            SetSurfaceHeight(surfaceHeight + delta);
        }
    }
}
