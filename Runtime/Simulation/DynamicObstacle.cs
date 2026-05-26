// =====================================================================
//  NATO C2 RTS Hybrid — DynamicObstacle.cs
//  ---------------------------------------------------------------------
//  Polygonal moving (or static) obstacle. Reports its current world
//  polygon to ORCA each Tick; ORCA applies stronger repulsion to
//  dynamic shapes via NATO_C2_Manager's ORCA.dynamicObstacleWeight.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2
{
    [DisallowMultipleComponent]
    [AddComponentMenu("NATO C2/Dynamic Obstacle")]
    public class DynamicObstacle : MonoBehaviour
    {
        [Header("Shape (local space)")]
        [Tooltip("CCW polygon describing the obstacle footprint, in this transform's local XZ space.")]
        public Vector2[] localPolygon = new[]
        {
            new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f)
        };

        [Header("Motion (optional)")]
        public bool isMoving = false;
        public Vector3 linearVelocity = Vector3.zero;
        public float angularSpeed = 0f;

        private readonly List<Vector3> _worldPolyCache = new List<Vector3>(8);

        private void OnEnable()
        {
            if (NATO_C2_Manager.Instance != null && NATO_C2_Manager.Instance.orca != null)
                NATO_C2_Manager.Instance.orca.RegisterDynamic(this);
        }
        private void OnDisable()
        {
            if (NATO_C2_Manager.Instance != null && NATO_C2_Manager.Instance.orca != null)
                NATO_C2_Manager.Instance.orca.UnregisterDynamic(this);
        }

        private void Update()
        {
            if (!isMoving) return;
            transform.position += linearVelocity * Time.deltaTime;
            if (angularSpeed != 0f)
                transform.Rotate(Vector3.up, angularSpeed * Time.deltaTime, Space.World);
        }

        /// <summary>World-space polygon used by ORCA each tick.</summary>
        public IEnumerable<Vector3> GetWorldPolygon()
        {
            _worldPolyCache.Clear();
            for (int i = 0; i < localPolygon.Length; i++)
            {
                Vector3 l = new Vector3(localPolygon[i].x, 0, localPolygon[i].y);
                _worldPolyCache.Add(transform.TransformPoint(l));
            }
            return _worldPolyCache;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.7f);
            if (localPolygon == null || localPolygon.Length < 2) return;
            for (int i = 0; i < localPolygon.Length; i++)
            {
                Vector3 a = transform.TransformPoint(new Vector3(localPolygon[i].x, 0, localPolygon[i].y));
                var n = localPolygon[(i + 1) % localPolygon.Length];
                Vector3 b = transform.TransformPoint(new Vector3(n.x, 0, n.y));
                Gizmos.DrawLine(a, b);
            }
        }
    }
}
