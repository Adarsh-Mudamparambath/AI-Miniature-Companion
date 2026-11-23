using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Mini.MR;

namespace Mini.Mobile
{
    /// <summary>
    /// Phone AR surface provider: raycasts against horizontal planes found by ARCore / ARKit.
    /// The plane the last successful ray hit becomes the surface Mini stands on, so voice moves
    /// ("come closer") stay on that table or floor.
    /// </summary>
    [DisallowMultipleComponent]
    public class ARPlaneSurfaceProvider : MonoBehaviour, ISpatialEnvironmentProvider
    {
        [SerializeField] private ARRaycastManager raycastManager;
        [SerializeField] private float maxRayDistance = 6f;
        [Tooltip("Minimum dot(plane normal, up) for a plane to count as a floor or table top.")]
        [SerializeField] private float minUpDot = 0.9f;

        private static readonly List<ARRaycastHit> Hits = new List<ARRaycastHit>();
        private float surfaceHeight;

        public string Name { get { return "AR planes"; } }
        public bool SupportsSurfaceDetection { get { return true; } }
        public float SurfaceHeight { get { return surfaceHeight; } }

        public void SetRaycastManager(ARRaycastManager manager)
        {
            if (manager != null) raycastManager = manager;
        }

        /// <summary>Hit-tests the AR planes. A hit also becomes the current surface height.</summary>
        public bool TryGetSurfacePose(Ray ray, out Pose pose)
        {
            pose = default(Pose);
            if (raycastManager == null) raycastManager = FindAnyObjectByType<ARRaycastManager>();
            if (raycastManager == null) return false;
            if (!raycastManager.Raycast(ray, Hits, TrackableType.PlaneWithinPolygon)) return false;

            // Hits are sorted nearest first.
            for (int i = 0; i < Hits.Count; i++)
            {
                ARRaycastHit hit = Hits[i];
                if (hit.distance > maxRayDistance) break;
                if (Vector3.Dot(hit.pose.up, Vector3.up) < minUpDot) continue;

                surfaceHeight = hit.pose.position.y;
                pose = new Pose(hit.pose.position, Quaternion.identity);
                return true;
            }
            return false;
        }

        public Vector3 ProjectToSurface(Vector3 worldPosition)
        {
            worldPosition.y = surfaceHeight;
            return worldPosition;
        }

        public void SetSurfaceHeight(float worldHeight)
        {
            surfaceHeight = worldHeight;
        }

        public void AdjustSurfaceHeight(float delta)
        {
            surfaceHeight += delta;
        }
    }
}
