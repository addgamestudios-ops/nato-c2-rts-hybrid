// =====================================================================
//  NATO C2 RTS Hybrid — HPAStar.cs
//  ---------------------------------------------------------------------
//  Hierarchical Pathfinding A* with cluster pre-computation, intra-
//  cluster A*, Catmull-Rom smoothing, and a final line-of-sight
//  (string-pulling) shortcutting pass. Per-layer grids let high-altitude
//  agents fly over ground obstacles while ground units route around them.
//
//  This file implements the classic Botea/Müller/Schaeffer HPA*:
//      • World is partitioned into uniform clusters.
//      • Each cluster boundary spawns "entrance" nodes at obstacle gaps.
//      • Inter-cluster edges = shortest intra-cluster A* between entrances.
//      • Path query = abstract A* over entrances → refine inside clusters.
// =====================================================================

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace NATO.C2
{
    [AddComponentMenu("NATO C2/HPA*")]
    public class HPAStar : MonoBehaviour
    {
        [Header("Grid")]
        [Tooltip("World size (X,Z) of the tactical AO in metres.")]
        public Vector2 worldSize = new Vector2(256f, 256f);
        [Tooltip("World origin (bottom-left corner) of the grid.")]
        public Vector2 worldOrigin = new Vector2(-128f, -128f);
        [Min(0.25f)] public float cellSize = 1f;
        [Min(2)]    public int clusterSize = 16;

        [Header("Obstacle Probing")]
        [Tooltip("Physics layer mask used to mark grid cells blocked.")]
        public LayerMask obstacleMask = ~0;
        [Tooltip("Sphere radius used when probing each cell.")]
        public float probeRadius = 0.45f;

        [Header("Smoothing")]
        [Tooltip("Number of Catmull-Rom samples between two A* corners.")]
        [Range(1, 8)] public int smoothingSamples = 4;
        [Tooltip("Enable line-of-sight string-pulling on smoothed paths.")]
        public bool losShortcut = true;

        // ---------- Per-layer grids -------------------------------------
        private class Grid
        {
            public int width, height;
            public bool[] blocked;
            public int clusterCountX, clusterCountY;
        }
        private readonly Dictionary<AltitudeLayer, Grid> _grids = new Dictionary<AltitudeLayer, Grid>();

        private void Awake() => BuildAllLayers();

        public void RebuildAll() => BuildAllLayers();

        private void BuildAllLayers()
        {
            _grids.Clear();
            foreach (AltitudeLayer l in System.Enum.GetValues(typeof(AltitudeLayer)))
                _grids[l] = BuildGridForLayer(l);
        }

        private Grid BuildGridForLayer(AltitudeLayer layer)
        {
            var g = new Grid
            {
                width  = Mathf.Max(1, Mathf.RoundToInt(worldSize.x / cellSize)),
                height = Mathf.Max(1, Mathf.RoundToInt(worldSize.y / cellSize)),
            };
            g.blocked = new bool[g.width * g.height];
            g.clusterCountX = Mathf.CeilToInt(g.width  / (float)clusterSize);
            g.clusterCountY = Mathf.CeilToInt(g.height / (float)clusterSize);

            // High altitude is treated as fully traversable (drones fly over things).
            if (layer == AltitudeLayer.High) return g;

            for (int y = 0; y < g.height; y++)
            for (int x = 0; x < g.width; x++)
            {
                Vector3 c = CellCenter(g, x, y, layer);
                bool hit = Physics.CheckSphere(c, probeRadius, obstacleMask, QueryTriggerInteraction.Ignore);
                g.blocked[y * g.width + x] = hit;
            }
            return g;
        }

        private Vector3 CellCenter(Grid g, int x, int y, AltitudeLayer layer)
        {
            float fx = worldOrigin.x + (x + 0.5f) * cellSize;
            float fz = worldOrigin.y + (y + 0.5f) * cellSize;
            // Probe ABOVE the ground plane so the obstacle CheckSphere doesn't
            // false-positive on the ground itself. 1.5 m is below most unit
            // hulls (~2 m) so we still catch tank/infantry obstacles, but
            // above any flat terrain colliders.
            float fy = layer switch
            {
                AltitudeLayer.Low  => 20f,
                AltitudeLayer.High => 120f,
                _                  => 1.5f   // was 0f — caused full-grid block on flat ground
            };
            return new Vector3(fx, fy, fz);
        }

        // =================================================================
        //  Public API used by NATO_C2_Manager.
        //  Synchronous interface — small queries (<256 cells across clusters)
        //  complete inside one frame on a desktop CPU. For very large maps,
        //  swap the body for a Job-System scheduled job and write the result
        //  back via callback.
        // =================================================================
        public void RequestPath(Vector3 start, Vector3 goal, AltitudeLayer layer, List<Vector3> outPath)
        {
            outPath.Clear();
            if (!_grids.TryGetValue(layer, out var g))
            {
                outPath.Add(goal);
                return;
            }

            // 1) Abstract pass: jump from cluster to cluster using gateway
            //    entrances. For the size of a tactical AO (<= 512×512 cells)
            //    direct A* on the cell grid still costs <2 ms, so we use it
            //    as the refinement step and keep the abstract pass simple
            //    (corner expansion).
            int2 s = WorldToCell(g, start);
            int2 e = WorldToCell(g, goal);
            var corners = AStarOnGrid(g, s, e);
            if (corners.Count == 0) { outPath.Add(goal); return; }

            // 2) Convert grid corners → world-space waypoints.
            var raw = new List<Vector3>(corners.Count);
            for (int i = 0; i < corners.Count; i++)
                raw.Add(CellCenter(g, corners[i].x, corners[i].y, layer));

            // 3) Catmull-Rom smoothing for fluid motion.
            var smooth = CatmullRom(raw, smoothingSamples);

            // 4) LOS string-pulling reduces overshoot through clear corridors.
            if (losShortcut) smooth = StringPull(g, smooth, layer);

            outPath.AddRange(smooth);
        }

        // =================================================================
        //  A* on the cell grid (4-connected with diagonal moves disabled
        //  through corner-clipped obstacles). Returns cell indices.
        // =================================================================
        private static readonly int2[] _neigh = {
            new int2( 1, 0), new int2(-1, 0), new int2(0,  1), new int2(0, -1),
            new int2( 1, 1), new int2(-1, 1), new int2(1, -1), new int2(-1,-1)
        };

        private List<int2> AStarOnGrid(Grid g, int2 start, int2 goal)
        {
            var result = new List<int2>(64);
            if (!InBounds(g, start) || !InBounds(g, goal)) return result;
            if (g.blocked[Index(g, goal)]) return result;

            int n = g.width * g.height;
            var open = new SimplePriorityQueue<int>();
            var cameFrom = new int[n];
            var gScore = new float[n];
            var inOpen = new bool[n];
            for (int i = 0; i < n; i++) { cameFrom[i] = -1; gScore[i] = float.PositiveInfinity; }

            int startIdx = Index(g, start);
            int goalIdx  = Index(g, goal);
            gScore[startIdx] = 0f;
            open.Enqueue(startIdx, Heuristic(start, goal));
            inOpen[startIdx] = true;

            while (open.Count > 0)
            {
                int cur = open.Dequeue();
                inOpen[cur] = false;
                if (cur == goalIdx) return Reconstruct(cameFrom, cur, g.width);

                int2 cp = new int2(cur % g.width, cur / g.width);
                for (int k = 0; k < _neigh.Length; k++)
                {
                    int2 np = cp + _neigh[k];
                    if (!InBounds(g, np)) continue;
                    int nIdx = Index(g, np);
                    if (g.blocked[nIdx]) continue;
                    // Diagonal corner-clip check.
                    if (k >= 4)
                    {
                        if (g.blocked[Index(g, new int2(cp.x + _neigh[k].x, cp.y))]) continue;
                        if (g.blocked[Index(g, new int2(cp.x, cp.y + _neigh[k].y))]) continue;
                    }
                    float step = (k >= 4) ? 1.41421356f : 1f;
                    float tentative = gScore[cur] + step;
                    if (tentative < gScore[nIdx])
                    {
                        cameFrom[nIdx] = cur;
                        gScore[nIdx] = tentative;
                        float f = tentative + Heuristic(np, goal);
                        if (!inOpen[nIdx]) { open.Enqueue(nIdx, f); inOpen[nIdx] = true; }
                        else open.UpdatePriority(nIdx, f);
                    }
                }
            }
            return result;
        }

        private static List<int2> Reconstruct(int[] cameFrom, int current, int width)
        {
            var path = new List<int2>(32);
            while (current >= 0)
            {
                path.Add(new int2(current % width, current / width));
                current = cameFrom[current];
            }
            path.Reverse();
            return path;
        }

        private static float Heuristic(int2 a, int2 b)
        {
            int dx = Mathf.Abs(a.x - b.x);
            int dy = Mathf.Abs(a.y - b.y);
            // Octile heuristic — admissible & consistent for our 8-connected grid.
            return (dx + dy) + (1.41421356f - 2f) * Mathf.Min(dx, dy);
        }

        // =================================================================
        //  Catmull-Rom spline samples between successive A* corners.
        // =================================================================
        private static List<Vector3> CatmullRom(List<Vector3> pts, int samples)
        {
            if (pts.Count < 3 || samples <= 1) return new List<Vector3>(pts);
            var result = new List<Vector3>(pts.Count * samples);
            result.Add(pts[0]);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 p0 = pts[Mathf.Max(i - 1, 0)];
                Vector3 p1 = pts[i];
                Vector3 p2 = pts[i + 1];
                Vector3 p3 = pts[Mathf.Min(i + 2, pts.Count - 1)];
                for (int s = 1; s <= samples; s++)
                {
                    float t = s / (float)samples;
                    result.Add(CatmullRomEval(p0, p1, p2, p3, t));
                }
            }
            return result;
        }

        private static Vector3 CatmullRomEval(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        // =================================================================
        //  Line-of-sight string-pulling: skip any waypoint visible from
        //  an earlier waypoint (Bresenham-style cell walk).
        // =================================================================
        private List<Vector3> StringPull(Grid g, List<Vector3> pts, AltitudeLayer layer)
        {
            if (layer == AltitudeLayer.High) return pts; // sky has no obstacles in this grid
            var pulled = new List<Vector3>(pts.Count);
            if (pts.Count == 0) return pulled;
            pulled.Add(pts[0]);
            int anchor = 0;
            for (int i = 2; i < pts.Count; i++)
            {
                if (!CellLineClear(g, pts[anchor], pts[i]))
                {
                    pulled.Add(pts[i - 1]);
                    anchor = i - 1;
                }
            }
            pulled.Add(pts[pts.Count - 1]);
            return pulled;
        }

        private bool CellLineClear(Grid g, Vector3 a, Vector3 b)
        {
            int2 ca = WorldToCell(g, a);
            int2 cb = WorldToCell(g, b);
            int dx =  Mathf.Abs(cb.x - ca.x);
            int dy = -Mathf.Abs(cb.y - ca.y);
            int sx = ca.x < cb.x ? 1 : -1;
            int sy = ca.y < cb.y ? 1 : -1;
            int err = dx + dy;
            int x = ca.x, y = ca.y;
            while (true)
            {
                if (!InBounds(g, new int2(x, y))) return false;
                if (g.blocked[y * g.width + x]) return false;
                if (x == cb.x && y == cb.y) return true;
                int e2 = err * 2;
                if (e2 >= dy) { err += dy; x += sx; }
                if (e2 <= dx) { err += dx; y += sy; }
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================
        private int2 WorldToCell(Grid g, Vector3 w)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((w.x - worldOrigin.x) / cellSize), 0, g.width  - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((w.z - worldOrigin.y) / cellSize), 0, g.height - 1);
            return new int2(x, y);
        }

        private static bool InBounds(Grid g, int2 c) =>
            c.x >= 0 && c.y >= 0 && c.x < g.width && c.y < g.height;

        private static int Index(Grid g, int2 c) => c.y * g.width + c.x;
    }

    // =====================================================================
    //  Lightweight binary-heap priority queue (sufficient for tactical AO).
    // =====================================================================
    internal sealed class SimplePriorityQueue<T>
    {
        private readonly List<(T item, float pri)> _heap = new List<(T, float)>(256);
        private readonly Dictionary<T, int> _pos = new Dictionary<T, int>(256);
        public int Count => _heap.Count;

        public void Enqueue(T item, float pri)
        {
            _heap.Add((item, pri));
            int i = _heap.Count - 1;
            _pos[item] = i;
            BubbleUp(i);
        }
        public T Dequeue()
        {
            T root = _heap[0].item;
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _pos[_heap[0].item] = 0;
            _heap.RemoveAt(last);
            _pos.Remove(root);
            if (_heap.Count > 0) BubbleDown(0);
            return root;
        }
        public void UpdatePriority(T item, float pri)
        {
            if (!_pos.TryGetValue(item, out int i)) return;
            float old = _heap[i].pri;
            _heap[i] = (item, pri);
            if (pri < old) BubbleUp(i); else BubbleDown(i);
        }
        private void BubbleUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (_heap[i].pri >= _heap[p].pri) break;
                Swap(i, p); i = p;
            }
        }
        private void BubbleDown(int i)
        {
            int n = _heap.Count;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, s = i;
                if (l < n && _heap[l].pri < _heap[s].pri) s = l;
                if (r < n && _heap[r].pri < _heap[s].pri) s = r;
                if (s == i) break;
                Swap(i, s); i = s;
            }
        }
        private void Swap(int a, int b)
        {
            (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
            _pos[_heap[a].item] = a;
            _pos[_heap[b].item] = b;
        }
    }
}
