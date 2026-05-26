// =====================================================================
//  NATO C2 RTS Hybrid — SelectionRingRenderer.cs
//  ---------------------------------------------------------------------
//  StarCraft-style ground decal under each unit. Visible only when the
//  Agent is selected (or when its control group is being highlighted).
//
//  Visual model (mirrors SC1/SC2 conventions):
//      • Bright outer ring (high alpha) in the affiliation colour
//      • Faint inner disc (low alpha) for footprint context
//      • Footprint radius scales with Agent.radius
//      • Pulses for 0.25s on the frame the unit becomes selected
//      • Tank-class units get a slightly larger ring than drones
//
//  Implementation: procedural mesh (triangle fan + ring strip) created
//  once on Awake, parented to the agent so it follows movement.
//  No texture needed — pure vertex colour.
// =====================================================================

using UnityEngine;

namespace NATO.C2.UI
{
    [RequireComponent(typeof(Agent))]
    [AddComponentMenu("NATO C2/Selection Ring Renderer")]
    public class SelectionRingRenderer : MonoBehaviour
    {
        [Header("Geometry")]
        [Tooltip("Footprint radius multiplier on top of Agent.radius.")]
        [Range(1.0f, 4.0f)] public float radiusMultiplier = 1.8f;
        [Tooltip("Ring thickness (outer−inner) as fraction of radius.")]
        [Range(0.05f, 0.30f)] public float ringThickness = 0.15f;
        [Tooltip("Y offset above ground to prevent z-fighting.")]
        public float groundOffset = 0.05f;

        [Header("Visual")]
        [Range(0f, 1f)] public float discAlpha = 0.18f;
        [Range(0f, 1f)] public float ringAlpha = 0.95f;
        [Tooltip("Bounce scale on first frame of selection.")]
        [Range(1.0f, 1.6f)] public float popScale = 1.25f;
        [Range(0.05f, 1.0f)] public float popDuration = 0.22f;

        // ---------- private state ---------------------------------------
        private Agent _agent;
        private GameObject _decal;
        private MeshRenderer _renderer;
        private Material _material;
        private Mesh _mesh;
        private bool _lastSelected;
        private float _selectedAtTime;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
            BuildDecal();
            Hide();
        }

        private void OnDestroy()
        {
            if (_material != null) DestroyImmediate(_material);
            if (_mesh != null) DestroyImmediate(_mesh);
        }

        // =================================================================
        //  Build the ring decal (mesh + material) once.
        // =================================================================
        private void BuildDecal()
        {
            _decal = new GameObject("SelectionRing");
            _decal.transform.SetParent(transform, false);
            _decal.transform.localPosition = new Vector3(0f, groundOffset - transform.position.y, 0f);
            _decal.transform.localRotation = Quaternion.identity;

            var mf = _decal.AddComponent<MeshFilter>();
            _renderer = _decal.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            _mesh = BuildRingMesh(64, 1.0f, 1.0f - ringThickness);
            mf.sharedMesh = _mesh;

            // Unlit transparent material with vertex colour driving alpha.
            _material = new Material(Shader.Find("Sprites/Default"));
            _material.color = Color.white;
            _material.renderQueue = 3100; // transparent, draws after opaque ground
            _renderer.sharedMaterial = _material;

            // Use the agent's hierarchy Y position so the decal stays on the ground.
            Vector3 wp = transform.position;
            _decal.transform.position = new Vector3(wp.x, groundOffset, wp.z);
        }

        // Triangle-fan disc + ring strip combined into one mesh.
        private Mesh BuildRingMesh(int segments, float outer, float inner)
        {
            var mesh = new Mesh { name = "SelectionRingMesh" };
            // Vertices: centre + outer ring + inner ring
            int n = segments;
            var verts = new Vector3[1 + n + n];
            var colors = new Color[verts.Length];
            verts[0] = Vector3.zero;
            colors[0] = new Color(1, 1, 1, discAlpha);
            for (int i = 0; i < n; i++)
            {
                float t = (i / (float)n) * Mathf.PI * 2f;
                float cx = Mathf.Cos(t), cz = Mathf.Sin(t);
                verts[1 + i]     = new Vector3(cx * outer, 0f, cz * outer);
                verts[1 + n + i] = new Vector3(cx * inner, 0f, cz * inner);
                // Outer = bright ring, inner = transparent (faded into disc).
                colors[1 + i]     = new Color(1, 1, 1, ringAlpha);
                colors[1 + n + i] = new Color(1, 1, 1, discAlpha);
            }

            // Triangles
            var tris = new int[n * 3 /* disc */ + n * 6 /* ring strip */];
            int idx = 0;
            // Filled disc (triangle fan from centre to inner ring).
            for (int i = 0; i < n; i++)
            {
                int a = 1 + n + i;
                int b = 1 + n + ((i + 1) % n);
                tris[idx++] = 0;
                tris[idx++] = b;
                tris[idx++] = a;
            }
            // Ring strip (inner → outer).
            for (int i = 0; i < n; i++)
            {
                int o0 = 1 + i;
                int o1 = 1 + ((i + 1) % n);
                int i0 = 1 + n + i;
                int i1 = 1 + n + ((i + 1) % n);
                tris[idx++] = i0;
                tris[idx++] = o1;
                tris[idx++] = o0;
                tris[idx++] = i0;
                tris[idx++] = i1;
                tris[idx++] = o1;
            }

            mesh.vertices  = verts;
            mesh.colors    = colors;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // =================================================================
        //  Tick — keep decal aligned, scale it for footprint + pop anim,
        //  toggle visibility on selection change.
        // =================================================================
        private void Update()
        {
            if (_agent == null) return;

            if (_agent.isSelected != _lastSelected)
            {
                _lastSelected = _agent.isSelected;
                if (_agent.isSelected)
                {
                    _selectedAtTime = Time.time;
                    Show();
                }
                else Hide();
            }

            if (!_agent.isSelected) return;

            // Footprint radius
            float r = Mathf.Max(0.5f, _agent.radius * radiusMultiplier);

            // Pop animation on first frame of selection.
            float since = Time.time - _selectedAtTime;
            float pop = 1f;
            if (since < popDuration)
            {
                float k = since / popDuration;
                // Ease-out overshoot.
                pop = Mathf.Lerp(popScale, 1f, 1f - (1f - k) * (1f - k));
            }
            float scale = r * pop;
            _decal.transform.localScale = new Vector3(scale, 1f, scale);

            // Keep the decal at Y = groundOffset in world space (don't inherit Y of the unit's transform).
            Vector3 wp = transform.position;
            _decal.transform.position = new Vector3(wp.x, groundOffset, wp.z);

            // Colour from affiliation.
            Color c = NATOPalette.For(_agent.affiliation);
            if (_material.color != c) _material.color = c;
        }

        private void Show()
        {
            if (_decal != null) _decal.SetActive(true);
        }
        private void Hide()
        {
            if (_decal != null) _decal.SetActive(false);
        }
    }
}
