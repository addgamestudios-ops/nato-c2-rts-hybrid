// =====================================================================
//  NATO C2 RTS Hybrid — DynamicObstacleSpawner.cs
//  ---------------------------------------------------------------------
//  Demo helper that periodically drops fresh obstacles into the AO.
//  Useful for stress-testing ORCA + HPA* re-routing.
// =====================================================================

using UnityEngine;

namespace NATO.C2
{
    [AddComponentMenu("NATO C2/Dynamic Obstacle Spawner")]
    public class DynamicObstacleSpawner : MonoBehaviour
    {
        [Header("Spawn Volume")]
        public Vector2 areaSize = new Vector2(80f, 80f);
        public Vector3 areaCenter = Vector3.zero;

        [Header("Cadence")]
        [Min(0.2f)] public float interval = 4f;
        [Min(1)]    public int maxAlive = 12;

        [Header("Template")]
        public DynamicObstacle obstaclePrefab;
        public bool movingObstacles = true;
        public float moveSpeedMax = 2.5f;

        private float _next;
        private int   _alive;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + interval;
            if (_alive >= maxAlive || obstaclePrefab == null) return;

            Vector3 pos = areaCenter + new Vector3(
                Random.Range(-areaSize.x * 0.5f, areaSize.x * 0.5f),
                0f,
                Random.Range(-areaSize.y * 0.5f, areaSize.y * 0.5f));
            var inst = Instantiate(obstaclePrefab, pos, Quaternion.Euler(0, Random.Range(0, 360f), 0));
            inst.isMoving = movingObstacles;
            inst.linearVelocity = movingObstacles
                ? new Vector3(Random.Range(-moveSpeedMax, moveSpeedMax), 0f, Random.Range(-moveSpeedMax, moveSpeedMax))
                : Vector3.zero;
            inst.angularSpeed = movingObstacles ? Random.Range(-15f, 15f) : 0f;
            _alive++;
            Destroy(inst.gameObject, 18f);
            inst.gameObject.AddComponent<AliveDecrementer>().host = this;
        }

        internal void Decrement() => _alive = Mathf.Max(0, _alive - 1);

        private class AliveDecrementer : MonoBehaviour
        {
            public DynamicObstacleSpawner host;
            private void OnDestroy() { if (host != null) host.Decrement(); }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.4f);
            Gizmos.DrawWireCube(areaCenter, new Vector3(areaSize.x, 0.1f, areaSize.y));
        }
    }
}
