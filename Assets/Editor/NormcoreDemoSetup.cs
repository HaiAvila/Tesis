using Normal;
using Normal.Realtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class NormcoreDemoSetup
{
    private const string AppKey = "436ff1c3-334d-4135-8da8-2dd54b53cd49";
    private const string SettingsFolder = "Assets/Settings";
    private const string SettingsPath = SettingsFolder + "/NormcoreAppSettings.asset";
    private const string ScenesFolder = "Assets/Scenes";
    private const string ScenePath = ScenesFolder + "/NormcoreDemo.unity";

    [MenuItem("Tools/Normcore/Create Demo Scene")]
    public static void CreateDemoScene()
    {
        if (AssetDatabase.LoadAssetAtPath<NormcoreAppSettings>(SettingsPath) != null)
            AssetDatabase.DeleteAsset(SettingsPath);

        var appSettings = NormcoreAppSettings.CreateInstance(AppKey, NormcoreAppSettings.DEFAULT_MATCHER_URL);
        if (!AssetDatabase.IsValidFolder(SettingsFolder))
            AssetDatabase.CreateFolder("Assets", "Settings");
        AssetDatabase.CreateAsset(appSettings, SettingsPath);
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var realtimeGO = new GameObject("Realtime");
        var realtime = realtimeGO.AddComponent<Realtime>();
        realtime.normcoreAppSettings = appSettings;

        var connector = realtimeGO.AddComponent<NormcoreDemoConnector>();
        connector.realtime = realtime;

        var cubeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeGO.name = "NetworkedCube";
        cubeGO.AddComponent<RealtimeView>();
        cubeGO.AddComponent<RealtimeTransform>();

        if (!AssetDatabase.IsValidFolder(ScenesFolder))
            AssetDatabase.CreateFolder("Assets", "Scenes");

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("Normcore demo scene created at " + ScenePath);
    }
}
