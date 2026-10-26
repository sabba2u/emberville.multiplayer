// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/BuildBundles.cs (Unity 6000.1.17f1).
// It adds the menu "Emberville > Build Bundles": every asset bundle you've named (a scene's AssetBundle name, at the bottom
// of its Inspector) is built for Windows into the project's "Bundles" folder, and copied into the game's
// BepInEx/plugins/EmbervilleGhost/Maps folder if it's where GamePath says. In the game: /room <name>.
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Our editing helpers (EmbervilleDoor...) come out of each room as it's built: the game hasn't got their scripts
/// (it reads the markers' names). Your scene keeps them.</summary>
class EmbervilleStripKitComponents : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (c && c.GetType().Name.StartsWith("Emberville")) Object.DestroyImmediate(c);
    }
}

public static class EmbervilleBuildBundles
{
    // where the game is (change it if yours is elsewhere; leave it wrong and the bundles just stay in Bundles/)
    public const string GamePath = @"K:\__games\steamapps\common\Emberville Demo";

    [MenuItem("Emberville/Build Bundles")]
    public static void Build()
    {
        string outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Bundles");
        Directory.CreateDirectory(outDir);
        var manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null) { Debug.LogError("Emberville: the bundles didn't build (see the errors above)"); return; }

        string maps = Path.Combine(GamePath, "BepInEx", "plugins", "EmbervilleGhost", "Maps");
        bool copy = Directory.Exists(Path.Combine(GamePath, "BepInEx"));
        if (copy) Directory.CreateDirectory(maps);
        foreach (var name in manifest.GetAllAssetBundles())
        {
            Debug.Log($"Emberville: built {name}");
            if (copy) File.Copy(Path.Combine(outDir, name), Path.Combine(maps, name), overwrite: true);
        }
        Debug.Log(copy ? $"Emberville: copied into {maps}. In the game: /room <name>" : $"Emberville: built into {outDir} (copy them into the game's BepInEx/plugins/EmbervilleGhost/Maps)");
        EditorUtility.RevealInFinder(outDir);
    }
}
