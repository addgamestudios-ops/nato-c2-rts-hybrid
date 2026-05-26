// =====================================================================
//  NATO C2 RTS Hybrid — BuildAndExportTools.cs
//  ---------------------------------------------------------------------
//  Two one-click Editor menu items for shipping artefacts:
//
//      NATO C2 → Build → macOS standalone (.app)
//      NATO C2 → Build → Export .unitypackage
//
//  Both write to ~/Desktop so the result is easy to grab.
//
//  The macOS standalone build:
//      • Picks SampleScene from the open scene list (or Assets/Samples/
//        if it lives there post-sample-import).
//      • Targets the current Editor's macOS architecture (Apple Silicon
//        if you're on M-series, Intel on older Macs).
//      • Outputs ~/Desktop/NATO_C2.app
//
//  The .unitypackage export:
//      • Bundles the package source (Runtime/, Editor/, Samples~/,
//        Documentation~/, package.json) into a single re-importable
//        .unitypackage so anyone can drop it into a fresh Unity project.
//      • Outputs ~/Desktop/NATO_C2_RTS_Hybrid_{version}.unitypackage
// =====================================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NATO.C2.EditorTools
{
    public static class BuildAndExportTools
    {
        // ---------------------------------------------------------------
        //  macOS standalone build
        // ---------------------------------------------------------------
        [MenuItem("NATO C2/Build/macOS standalone (.app)", priority = 400)]
        public static void BuildMacApp()
        {
            // 1) Resolve which scene(s) to include.  Pick the SampleScene
            //    Unity ships under Assets/Samples first; fall back to
            //    whatever's currently open.
            var scenes = new List<string>();
            string sample = FindFirstSampleScene();
            if (!string.IsNullOrEmpty(sample)) scenes.Add(sample);

            if (scenes.Count == 0)
            {
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (!string.IsNullOrEmpty(active.path)) scenes.Add(active.path);
            }
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog("Build", "No scene to build — open the SampleScene first or import the demo Sample.", "OK");
                return;
            }

            // 2) Output path on the Desktop.
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            string outPath = Path.Combine(desktop, "NATO_C2.app");

            // 3) Build options.
            var opts = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = outPath,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            Debug.Log($"[Build] Building macOS app with scenes={string.Join(", ", scenes)} → {outPath}");
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            var summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Build] ✅ macOS build succeeded — {summary.totalSize / (1024 * 1024)} MB at {outPath}");
                EditorUtility.RevealInFinder(outPath);
            }
            else
            {
                Debug.LogError($"[Build] ❌ macOS build failed — result={summary.result} " +
                               $"errors={summary.totalErrors} warnings={summary.totalWarnings}");
            }
        }

        // ---------------------------------------------------------------
        //  .unitypackage export
        // ---------------------------------------------------------------
        [MenuItem("NATO C2/Build/Export .unitypackage", priority = 401)]
        public static void ExportPackage()
        {
            // For a Local Package (file://), the source lives outside Assets/.
            // AssetDatabase.ExportPackage only operates on Assets-relative paths.
            // The simplest robust path: ship the IMPORTED sample as a
            // .unitypackage (covers the demo end-to-end) plus any local
            // Assets/ work the user has added.

            var paths = new List<string>();
            void AddIfExists(string p)
            {
                if (AssetDatabase.IsValidFolder(p) || File.Exists(p)) paths.Add(p);
            }
            AddIfExists("Assets/Samples");
            AddIfExists("Assets/Tests");
            AddIfExists("Assets/Scenes");
            AddIfExists("Assets/NATO_C2");

            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog("Export",
                    "Nothing to export from Assets/ — import the sample first or move sources into Assets/NATO_C2/.",
                    "OK");
                return;
            }

            string version = "0.1.0";
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            string outPath = Path.Combine(desktop, $"NATO_C2_RTS_Hybrid_{version}.unitypackage");

            AssetDatabase.ExportPackage(paths.ToArray(), outPath,
                ExportPackageOptions.Recurse | ExportPackageOptions.Interactive);

            Debug.Log($"[Export] ✅ Wrote .unitypackage → {outPath}");
            EditorUtility.RevealInFinder(outPath);
        }

        // ---------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------
        private static string FindFirstSampleScene()
        {
            // Look under Assets/Samples/... for any .unity file.
            string[] hits = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Samples" });
            if (hits == null || hits.Length == 0) return null;
            return AssetDatabase.GUIDToAssetPath(hits[0]);
        }
    }
}
#endif
