// =====================================================================
//  NATO C2 RTS Hybrid — FormationController.cs
//  ---------------------------------------------------------------------
//  Computes per-Agent slot offsets for Wedge, Line, and Circle formations
//  and exposes a live-preview API the HUD draws while the player drags
//  out a move command. Slots are assigned via a Hungarian-style greedy
//  match on (current position → slot offset) so units don't cross paths.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2
{
    public enum FormationType
    {
        Wedge,
        Line,
        Circle,
        Column,
        Free
    }

    [AddComponentMenu("NATO C2/Formation Controller")]
    public class FormationController : MonoBehaviour
    {
        [Header("Formation Selection")]
        public FormationType active = FormationType.Wedge;

        [Header("Spacing")]
        [Min(0.5f)] public float spacing = 2.5f;
        [Tooltip("Wedge half-angle in degrees from the facing axis.")]
        [Range(15f, 75f)] public float wedgeAngle = 35f;

        // =================================================================
        //  Slot assignment — fills Agent.formationSlot in local-to-target space.
        // =================================================================
        public void AssignSlots(IReadOnlyList<Agent> agents, Vector3 worldTarget)
        {
            if (agents == null || agents.Count == 0) return;

            // 1) Generate ideal slot offsets in formation-local coordinates
            //    where +Z = facing direction toward the target.
            Vector3 origin = AverageOrigin(agents);
            Vector3 facing = (worldTarget - origin); facing.y = 0f;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.forward;
            facing.Normalize();

            var slots = GenerateSlots(agents.Count, facing);

            // 2) Greedy match: pair each agent with its nearest still-unused slot.
            var used = new bool[slots.Count];
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null) continue;
                int bestSlot = -1;
                float bestDist = float.PositiveInfinity;
                Vector3 from = a.transform.position - worldTarget;
                for (int s = 0; s < slots.Count; s++)
                {
                    if (used[s]) continue;
                    float d = (slots[s] - from).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; bestSlot = s; }
                }
                if (bestSlot >= 0)
                {
                    used[bestSlot] = true;
                    a.formationSlot = slots[bestSlot];
                }
                else
                {
                    a.formationSlot = Vector3.zero;
                }
            }
        }

        // =================================================================
        //  Preview — used by the HUD to draw ghost markers under the cursor
        //  while the player is choosing a move/attack target.
        // =================================================================
        public List<Vector3> PreviewSlots(int count, Vector3 worldTarget, Vector3 facing)
        {
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.forward;
            facing.Normalize();
            var slots = GenerateSlots(count, facing);
            for (int i = 0; i < slots.Count; i++) slots[i] += worldTarget;
            return slots;
        }

        // =================================================================
        //  Generators
        // =================================================================
        private List<Vector3> GenerateSlots(int count, Vector3 facing)
        {
            switch (active)
            {
                case FormationType.Wedge:  return Wedge(count, facing);
                case FormationType.Line:   return Line(count, facing);
                case FormationType.Circle: return Circle(count);
                case FormationType.Column: return Column(count, facing);
                default:                    return Free(count);
            }
        }

        private List<Vector3> Wedge(int count, Vector3 facing)
        {
            var list = new List<Vector3>(count);
            Vector3 right = Vector3.Cross(Vector3.up, facing).normalized;
            float angRad = wedgeAngle * Mathf.Deg2Rad;
            float dz = -spacing * Mathf.Cos(angRad);
            float dx =  spacing * Mathf.Sin(angRad);
            list.Add(Vector3.zero);
            int placed = 1;
            int row = 1;
            while (placed < count)
            {
                Vector3 baseRow = facing * dz * row;
                for (int side = -1; side <= 1; side += 2)
                {
                    if (placed >= count) break;
                    list.Add(baseRow + right * (dx * row * side));
                    placed++;
                }
                row++;
            }
            return list;
        }

        private List<Vector3> Line(int count, Vector3 facing)
        {
            var list = new List<Vector3>(count);
            Vector3 right = Vector3.Cross(Vector3.up, facing).normalized;
            float halfWidth = (count - 1) * spacing * 0.5f;
            for (int i = 0; i < count; i++)
                list.Add(right * (i * spacing - halfWidth));
            return list;
        }

        private List<Vector3> Column(int count, Vector3 facing)
        {
            var list = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
                list.Add(-facing * (i * spacing));
            return list;
        }

        private List<Vector3> Circle(int count)
        {
            var list = new List<Vector3>(count);
            float radius = Mathf.Max(1f, count * spacing / (2f * Mathf.PI));
            for (int i = 0; i < count; i++)
            {
                float t = (i / (float)count) * Mathf.PI * 2f;
                list.Add(new Vector3(Mathf.Cos(t), 0, Mathf.Sin(t)) * radius);
            }
            return list;
        }

        private List<Vector3> Free(int count)
        {
            var list = new List<Vector3>(count);
            for (int i = 0; i < count; i++) list.Add(Vector3.zero);
            return list;
        }

        private static Vector3 AverageOrigin(IReadOnlyList<Agent> agents)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i] == null) continue;
                sum += agents[i].transform.position;
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }
    }
}
