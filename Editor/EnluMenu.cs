#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Enlu.Editor
{
    /// <summary>Menu items for creating and finding the ENLU settings asset.</summary>
    public static class EnluMenu
    {
        /// <summary>Assets &gt; Create &gt; ENLU &gt; Settings: creates a settings asset in the selected folder.</summary>
        [MenuItem("Assets/Create/ENLU/Settings", false, 200)]
        public static void CreateSettings()
        {
            var folder = SelectedFolder();
            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + EnluSettings.ResourceName + ".asset");
            var asset = ScriptableObject.CreateInstance<EnluSettings>();
            ProjectWindowUtil.CreateAsset(asset, path);
        }

        /// <summary>
        /// Tools &gt; ENLU &gt; Settings: selects <c>Resources/EnluSettings</c>, creating
        /// <c>Assets/Resources/EnluSettings.asset</c> when there is none.
        /// </summary>
        [MenuItem("Tools/ENLU/Settings", false, 0)]
        public static void SelectOrCreateSettings()
        {
            var existing = EnluSettings.Load();
            if (existing == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
                existing = ScriptableObject.CreateInstance<EnluSettings>();
                AssetDatabase.CreateAsset(existing, "Assets/Resources/" + EnluSettings.ResourceName + ".asset");
                AssetDatabase.SaveAssets();
            }
            Selection.activeObject = existing;
            EditorGUIUtility.PingObject(existing);
        }

        private static string SelectedFolder()
        {
            var path = "Assets";
            foreach (var obj in Selection.GetFiltered<Object>(SelectionMode.Assets))
            {
                var p = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(p)) continue;
                path = Directory.Exists(p) ? p : Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "Assets";
                break;
            }
            return path;
        }
    }
}
