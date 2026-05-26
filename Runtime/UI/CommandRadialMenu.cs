// =====================================================================
//  NATO C2 RTS Hybrid — CommandRadialMenu.cs
//  ---------------------------------------------------------------------
//  Right-click radial command wheel. Six wedges: MOVE, ATTACK, LOITER,
//  SWARM, RTB, HOLD. Modern sticky UX:
//
//      RMB-down anywhere on the AO → menu opens at the cursor
//      Move cursor over a wedge      → wedge highlights cyan, others dim
//      LMB-click on a wedge          → command issued, menu closes
//      ESC or LMB-click outside      → menu cancels with no command
//
//  No hold-and-release required. Forgiving hit zones, instant feedback.
// =====================================================================

using System;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [AddComponentMenu("NATO C2/Command Radial Menu")]
    public class CommandRadialMenu : MonoBehaviour
    {
        [Header("Refs")]
        public RectTransform root;        // canvas group root we show/hide
        public CanvasGroup   canvasGroup;
        public Image[]       wedgeImages = new Image[6];
        public Text[]        wedgeLabels = new Text[6];

        [Header("Tuning")]
        [Tooltip("Inside this radius (px) the cursor is in the dead zone and no wedge highlights.")]
        public float innerRadius = 48f;
        [Tooltip("Outside this radius the menu closes (cancel).")]
        public float outerRadius = 200f;
        [Tooltip("Cosmetic — duration of the fade in.")]
        public float fadeDuration = 0.08f;

        private static readonly (CommandOrder order, string label)[] _order = {
            (CommandOrder.Move,   "MOVE"),
            (CommandOrder.Attack, "ATTACK"),
            (CommandOrder.Loiter, "LOITER"),
            (CommandOrder.Swarm,  "SWARM"),
            (CommandOrder.RTB,    "RTB"),
            (CommandOrder.Hold,   "HOLD"),
        };

        public bool IsOpen { get; private set; }
        private Vector3 _worldTarget;
        private float _openedAtTime;

        public event Action<CommandOrder, Vector3> OnOrderCommitted;

        private void Awake()
        {
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            if (root != null) root.gameObject.SetActive(false);
            for (int i = 0; i < wedgeLabels.Length && i < _order.Length; i++)
                if (wedgeLabels[i] != null) wedgeLabels[i].text = _order[i].label;
        }

        public void Open(Vector3 worldTarget)
        {
            IsOpen = true;
            _worldTarget = worldTarget;
            _openedAtTime = Time.unscaledTime;
            if (root != null)
            {
                root.gameObject.SetActive(true);
                root.position = Input.mousePosition;
            }
            if (canvasGroup != null) canvasGroup.alpha = 1f;
        }

        public void Close()
        {
            IsOpen = false;
            if (root != null) root.gameObject.SetActive(false);
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            // Reset visuals.
            for (int i = 0; i < wedgeImages.Length; i++)
            {
                if (wedgeImages[i] == null) continue;
                wedgeImages[i].color = new Color(1f, 1f, 1f, 0.20f);
                if (wedgeLabels[i] != null) wedgeLabels[i].color = NATOPalette.AccentCyan;
            }
        }

        private void Update()
        {
            if (!IsOpen) return;

            // ---- compute hover wedge from cursor offset from menu centre ----
            int hover = -1;
            if (root != null)
            {
                Vector2 delta = (Vector2)Input.mousePosition - (Vector2)root.position;
                float d = delta.magnitude;
                if (d >= innerRadius)
                {
                    float ang = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                    if (ang < 0f) ang += 360f;
                    float shifted = (ang - 60f + 360f) % 360f;
                    hover = Mathf.Clamp(Mathf.FloorToInt(shifted / 60f), 0, 5);
                }
            }

            // ---- visual update: hovered wedge cyan-bright, others dim ----
            for (int i = 0; i < wedgeImages.Length; i++)
            {
                if (wedgeImages[i] == null) continue;
                if (i == hover)
                {
                    wedgeImages[i].color = NATOPalette.AccentCyan;
                    if (wedgeLabels[i] != null)
                        wedgeLabels[i].color = NATOPalette.BackgroundBlue;
                }
                else
                {
                    wedgeImages[i].color = new Color(1f, 1f, 1f, 0.15f);
                    if (wedgeLabels[i] != null)
                        wedgeLabels[i].color = NATOPalette.AccentCyan;
                }
            }

            // ---- commit / cancel ----
            // Commit on LMB-UP (not down) so the TacticalHUD's HandleSelection
            // doesn't see the bleeding LMB-down on the same frame the menu closes,
            // which would otherwise clear the selection. UP is also more SC2-like.
            bool acceptingClicks = (Time.unscaledTime - _openedAtTime) > 0.08f;

            if (acceptingClicks && Input.GetMouseButtonUp(0))
            {
                if (hover >= 0 && hover < _order.Length)
                {
                    var ord = _order[hover].order;
                    OnOrderCommitted?.Invoke(ord, _worldTarget);
                    if (NATO_C2_Manager.Instance != null)
                        NATO_C2_Manager.Instance.IssueCommand(ord, _worldTarget);
                }
                Close();
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
            }
            else if (Input.GetMouseButtonDown(1))
            {
                // Second right-click cancels the menu (toggle behaviour).
                Close();
            }
        }
    }
}
