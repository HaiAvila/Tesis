using System;
using System.IO;
using UnityEngine;

// Where this app keeps its data: the shot list database, the takes' audio, the voice notes, the
// exports. Application.persistentDataPath on every device - except the server .exe, which gets
// its own "Servidor" subfolder. On a PC that also runs the Editor (or a PC build of another
// role) both would otherwise open the same database file while acting as two different devices:
// the Editor and every player of this project share one persistentDataPath.
//
// "-datos <name>" on a player's command line picks another subfolder, to run a second copy of
// any role on the same PC as if it were another device (testing only). In the Editor the same
// comes from SessionState "Tesis.CarpetaDatos" (set from a script; forgotten when the Editor
// closes), to try the syncing against copies instead of the real takes.
//
// Main thread only the first time (Application.persistentDataPath).
public static class AppPaths
{
    const string ServerFolder = "Servidor";
    const string FolderArgument = "-datos";

    static string dataRoot;

#if UNITY_EDITOR
    // Play mode without a domain reload keeps statics: read the folder again each run.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetForPlayMode() => dataRoot = null;
#endif

    public static string DataRoot
    {
        get
        {
            if (dataRoot == null)
                dataRoot = Resolve();
            return dataRoot;
        }
    }

    static string Resolve()
    {
        string root = Application.persistentDataPath;
        string folder = null;

#if !UNITY_EDITOR
#if SERVER_BUILD
        folder = ServerFolder;
#endif
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == FolderArgument && IsSafeName(args[i + 1]))
                folder = args[i + 1];
        }
#endif

#if UNITY_EDITOR
        string testFolder = UnityEditor.SessionState.GetString("Tesis.CarpetaDatos", "");
        if (IsSafeName(testFolder))
        {
            folder = testFolder;
            Debug.LogWarning($"[Datos] TEST DATA FOLDER \"{testFolder}\": this Editor session is not using the real takes");
        }
#endif

        if (string.IsNullOrEmpty(folder))
            return root;

        root = Path.Combine(root, folder);
        Directory.CreateDirectory(root);
        Debug.Log($"[Datos] this copy keeps its data in {root}");
        return root;
    }

    static bool IsSafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 40)
            return false;
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }
}
