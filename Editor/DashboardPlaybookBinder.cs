// =====================================================================
//  NATO C2 RTS Hybrid — DashboardPlaybookBinder.cs
//  ---------------------------------------------------------------------
//  Editor-only glue that wires the runtime dashboard's symptom-detected
//  CTAs to the FederationPlaybookWindow. Lives in the Editor assembly
//  so the Runtime doesn't take a dependency on EditorWindow.
//
//  Static constructor hooks the runtime's
//  Link16FederationDashboard.OnOpenPlaybookRequested action exactly
//  once per domain load.
// =====================================================================

#if UNITY_EDITOR
using UnityEditor;
using NATO.C2.UI;

namespace NATO.C2.EditorTools
{
    [InitializeOnLoad]
    public static class DashboardPlaybookBinder
    {
        static DashboardPlaybookBinder()
        {
            Link16FederationDashboard.OnOpenPlaybookRequested = OpenForSymptom;
        }

        private static void OpenForSymptom(string symptomHint)
        {
            FederationPlaybookWindow.OpenForSymptom(symptomHint);
        }
    }
}
#endif
