// =====================================================================
//  NATO C2 RTS Hybrid — CommandPing.cs
//  ---------------------------------------------------------------------
//  Spawned at the point an order lands to give the operator instant
//  visual confirmation of where the order was placed. Same pattern as
//  SC2's "move arrow" or "attack target" flash:
//
//      MOVE   →  green ring expanding from the click point
//      ATTACK →  red crosshair pulsing inward
//      OTHER  →  cyan ring (LOITER, SWARM, RTB, HOLD, etc.)
//
//  Auto-destroys after duration. Stacks safely if the operator chains
//  orders — each ping owns its own decal.
// =====================================================================

using UnityEngine;

namespace NATO.C2.UI
{
    [AddComponentMenu("NATO C2/Command Ping")]
    public class CommandPing : MonoBehaviour
    {
        public enum Kind { Move, Attack, Other }

        [Header("Animation")]
        [Min(0.1f)] public float duration = 0.65f;
        [Min(0.5f)] public float startRadius = 0.5f;
        [Min(1f)]   public float endRadius   = 6f;
        public float yOffset = 0.06f;

        private MeshRenderer _renderer;
        private Material _material;
        private Mesh _mesh;
        private float _spawnedAt;
        private Color _color;
        private Kind _kind;

        public static CommandPing Spawn(Vector3 worldPos, Kind kind)
        {
            var go = new GameObject($"CommandPing_{kind}");
            go.transform.position = new Vector3(worldPos.x, 0.06f, worldPos.z);
            var p = go.AddComponent<CommandPing>();
            p.Init(kind);
            return p;
        }

        private void Init(Kind kind)
        {
            _kind = kind;
            _color = kind switch
            {
                Kind.Move   => NATOPalette.FriendlyGreen,
                Kind.Attack => NATOPalette.HostileRed,
                _           => NATOPalette.AccentCyan
            };

            var mf = gameObject.AddComponent<MeshFilter>();
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            _mesh = BuildRing(64, 1.0f, 0.85f, kind == Kind.Attack);
            mf.sharedMesh = _mesh;

            _material = new Material(Shader.Find("Sprites/Default"));
            _material.color = _color;
            _material.renderQueue = 3100;
            _renderer.sharedMaterial = _material;

            _spawnedAt = Time.time;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_mesh != null) Destroy(_mesh);
        }

        private void Update()
        {
            float t = (Time.time - _spawnedAt) / Mathf.Max(0.01f, duration);
            if (t >= 1f) { Destroy(gameObject); return; }

            // Move/Other = expand outward; Attack = contract inward (crosshair lock).
            float r;
            float alpha;
            if (_kind == Kind.Attack)
            {
                r = Mathf.Lerp(endRadius, startRadius, t);
                alpha = 1f - t; // fade out
            }
            else
            {
                r = Mathf.Lerp(startRadius, endRadius, t);
                alpha = 1f - t;
            }
            transform.localScale = new Vector3(r, 1f, r);

            Color c = _color;
            c.a = alpha;
            _material.color = c;
        }

        private static Mesh BuildRing(int segments, float outer, float inner, bool addCrosshair)
        {
            var m = new Mesh { name = "CommandPingRing" };
            int n = segments;
            int crossVerts = addCrosshair ? 8 : 0;
            var verts = new Vector3[n + n + crossVerts];
            var cols  = new Color[verts.Length];
            for (int i = 0; i < n; i++)
            {
                float t = (i / (float)n) * Mathf.PI * 2f;
                float cx = Mathf.Cos(t), cz = Mathf.Sin(t);
                verts[i]     = new Vector3(cx * outer, 0f, cz * outer);
                verts[n + i] = new Vector3(cx * inner, 0f, cz * inner);
                cols[i] = cols[n + i] = Color.white;
            }
            if (addCrosshair)
            {
                int b = n + n;
                float l = 0.85f, t2 = 0.06f;
                verts[b + 0] = new Vector3(-l,  0,  t2); verts[b + 1] = new Vector3( l, 0,  t2);
                verts[b + 2] = new Vector3(-l,  0, -t2); verts[b + 3] = new Vector3( l, 0, -t2);
                verts[b + 4] = new Vector3( t2, 0,  l); verts[b + 5] = new Vector3(-t2, 0,  l);
                verts[b + 6] = new Vector3( t2, 0, -l); verts[b + 7] = new Vector3(-t2, 0, -l);
                for (int i = 0; i < 8; i++) cols[b + i] = Color.white;
            }

            int triCount = n * 6 + (addCrosshair ? 12 : 0);
            var tris = new int[triCount];
            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                int o0 = i, o1 = (i + 1) % n;
                int i0 = n + i, i1 = n + ((i + 1) % n);
                tris[idx++] = i0; tris[idx++] = o1; tris[idx++] = o0;
                tris[idx++] = i0; tris[idx++] = i1; tris[idx++] = o1;
            }
            if (addCrosshair)
            {
                int b = n + n;
                tris[idx++] = b + 0; tris[idx++] = b + 1; tris[idx++] = b + 3;
                tris[idx++] = b + 0; tris[idx++] = b + 3; tris[idx++] = b + 2;
                tris[idx++] = b + 4; tris[idx++] = b + 5; tris[idx++] = b + 7;
                tris[idx++] = b + 4; tris[idx++] = b + 7; tris[idx++] = b + 6;
            }
            m.vertices = verts;
            m.colors   = cols;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }
    }
}
