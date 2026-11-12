using System.IO;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Tesis > Build > Servidor (.exe): builds the server with the "PC Server" build profile, which is
// always SERVER_BUILD (RoleSwitcher leaves it alone), into Builds/Servidor/. The Editor's own
// role and active build profile are put back when it finishes. Copy that whole folder to the actor's PC;
// Servidor.exe is what they double-click.
[InitializeOnLoad]
static class ServerBuilder
{
    const string ProfilePath = "Assets/Settings/Build Profiles/" + RoleSwitcher.ServerProfileName + ".asset";
    public const string OutputFolder = "Builds/Servidor";
    public const string ExeName = "Servidor.exe";

    // Building with a profile makes it the active one, a moment after the build returns - and
    // the Editor would stay compiled as the server. The profile that was active is remembered
    // here (it survives the recompilation in between) and put back once the Editor is idle.
    const string RestoreKey = "Tesis.ServerBuilder.RestoreProfile";
    const string RestoreSinceKey = "Tesis.ServerBuilder.RestoreSince";
    const float RestoreWatchSeconds = 20f;

    static ServerBuilder()
    {
        EditorApplication.update += RestoreProfileWhenIdle;
    }

    static void RestoreProfileWhenIdle()
    {
        string path = SessionState.GetString(RestoreKey, "");
        if (path.Length == 0)
            return;
        if (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        BuildProfile previous = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
        if (previous == null)
        {
            SessionState.EraseString(RestoreKey);
            return;
        }

        if (BuildProfile.GetActiveBuildProfile() == previous)
        {
            // Not switched (yet): keep watching for a while, then stop.
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(RestoreSinceKey, 0f) > RestoreWatchSeconds)
                SessionState.EraseString(RestoreKey);
            return;
        }

        SessionState.EraseString(RestoreKey);
        BuildProfile.SetActiveBuildProfile(previous);
        Debug.Log($"[ServerBuilder] active build profile back to {previous.name}");
    }

    [MenuItem("Tesis/Build/Servidor (.exe)")]
    static void BuildFromMenu()
    {
        if (Build())
            EditorUtility.RevealInFinder(Path.Combine(OutputFolder, ExeName));
    }

    // The director's desktop build (DesktopDirector: mouse and keyboard, no headset): the "PC
    // Director" profile, which takes the Editor's role - so the role must be Director.
    public const string DirectorFolder = "Builds/Director";
    public const string DirectorExe = "Director.exe";

    [MenuItem("Tesis/Build/Director escritorio (.exe)")]
    static void BuildDirectorFromMenu()
    {
        if (BuildDirector())
            EditorUtility.RevealInFinder(Path.Combine(DirectorFolder, DirectorExe));
    }

    public static bool BuildDirector()
    {
#if DIRECTOR_BUILD
        return Build("Assets/Settings/Build Profiles/PC Director.asset", DirectorFolder, DirectorExe);
#else
        EditorUtility.DisplayDialog("Director escritorio", "Cambia el rol a Director primero (Tesis → Role → Director).", "OK");
        return false;
#endif
    }

    // Also callable from the unity CLI. True if the build succeeded.
    public static bool Build() => Build(ProfilePath, OutputFolder, ExeName);

    static bool Build(string profilePath, string outputFolder, string exeName)
    {
        BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(profilePath);
        if (profile == null)
        {
            Debug.LogError($"[ServerBuilder] build profile not found: {profilePath}");
            return false;
        }

        BuildProfile previous = BuildProfile.GetActiveBuildProfile();
        if (previous != null && previous != profile)
            SessionState.SetString(RestoreKey, AssetDatabase.GetAssetPath(previous));

        Directory.CreateDirectory(outputFolder);
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions
        {
            buildProfile = profile,
            locationPathName = Path.Combine(outputFolder, exeName),
            options = BuildOptions.None,
        });
        SessionState.SetFloat(RestoreSinceKey, (float)EditorApplication.timeSinceStartup);

        BuildSummary summary = report.summary;
        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[ServerBuilder] the build of {exeName} failed: {summary.result}, {summary.totalErrors} errors");
            return false;
        }

        Debug.Log($"[ServerBuilder] built: {summary.outputPath} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:F0} s)");
        return true;
    }
}
