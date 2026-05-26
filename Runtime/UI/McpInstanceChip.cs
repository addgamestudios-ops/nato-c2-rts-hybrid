// =====================================================================
//  NATO C2 RTS Hybrid — McpInstanceChip.cs
//  ---------------------------------------------------------------------
//  Tiny top-right UI chip that displays this Unity instance's MCP
//  registration name (e.g. "alpha", "bravo") so the operator can tell
//  at a glance whether Claude is currently driving THIS Unity or a
//  peer's. Mirrors the Lattice top-bar styling.
//
//  Reads the NATO_MCP_INSTANCE_NAME environment variable at Start.
//  When unset (the default), the chip stays hidden — single-instance
//  setups get no extra UI clutter.
//
//  Operators set the env in the launch script:
//      NATO_MCP_INSTANCE_NAME=alpha /Applications/Unity/Unity.app/...
//  This is the SAME name that gets registered in the MCP server's
//  UNITY_MCP_INSTANCES csv so naming stays consistent across the
//  stack.
// =====================================================================

using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(46)]
    [AddComponentMenu("NATO C2/MCP Instance Chip")]
    public class McpInstanceChip : MonoBehaviour
    {
        [Tooltip("Anchor in the parent Canvas. (1,1) = top-right.")]
        public Vector2 anchor = new Vector2(1f, 1f);
        [Tooltip("Pixel offset from the anchor (negative x = left, negative y = down).")]
        public Vector2 offset = new Vector2(-12f, -50f);

        [Tooltip("Override the env-derived instance name. Empty = read NATO_MCP_INSTANCE_NAME.")]
        public string explicitInstanceName = "";

        private void Start()
        {
            string name = !string.IsNullOrEmpty(explicitInstanceName)
                ? explicitInstanceName
                : (System.Environment.GetEnvironmentVariable("NATO_MCP_INSTANCE_NAME") ?? "");
            if (string.IsNullOrEmpty(name))
            {
                // Single-instance / unconfigured: hide the chip.
                gameObject.SetActive(false);
                return;
            }
            BuildChip(name);
        }

        private void BuildChip(string instanceName)
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[McpInstanceChip] needs a parent Canvas — disabling.");
                enabled = false;
                return;
            }

            var go = new GameObject("McpInstanceChip", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot     = new Vector2(1f, 1f);
            rt.anchoredPosition = offset;
            rt.sizeDelta = new Vector2(110, 22);
            go.GetComponent<Image>().color = new Color(0.40f, 0.85f, 1.00f, 0.85f);  // Lattice accent

            var lblGo = new GameObject("Lbl", typeof(RectTransform), typeof(Text));
            lblGo.transform.SetParent(go.transform, false);
            var lblRt = (RectTransform)lblGo.transform;
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = Vector2.zero; lblRt.offsetMax = Vector2.zero;
            var t = lblGo.GetComponent<Text>();
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = 11;
            t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.color     = Color.black;
            t.text      = "MCP: " + instanceName;
        }
    }
}
