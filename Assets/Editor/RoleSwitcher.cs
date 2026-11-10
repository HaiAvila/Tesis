using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Compilation;
using UnityEngine;

// One-click build role: Tesis > Role > Director / Actor / Server. The single source of the
// role defines (DIRECTOR_BUILD, SERVER_BUILD; none = actor).
//
// The role goes into every build profile's own Scripting Defines list, not into Player
// Settings. Player Settings can't hold it reliably: profiles without their own Player
// Settings (the PC ones) share the global ones, and profiles with their own (the Quest ones,
// which carry real Quest settings) each keep a separate copy that Unity offers no public API
// to edit while that profile isn't active. A profile's Scripting Defines are added on top of
// whichever Player Settings it uses whenever it's active, so writing the role to all
// profiles makes it apply on Android (Quest) and Standalone (PC) alike, whichever profile is
// active. Every other define is left untouched, and no Player Settings hold a role define.
//
// The role only takes effect while a build profile is active - with none active (plain
// platform settings) the Editor compiles as actor, and the switcher warns about it.
//
// "PC Server" is the exception: that profile is always the server, whatever the role here, so
// Tesis > Build > Servidor (.exe) (ServerBuilder) can build the server without changing what
// the Editor is compiled as. Role > Server still exists, to run the server in the Editor.
static class RoleSwitcher
{
    enum Role { Director, Actor, Server }

    const string DirectorDefine = "DIRECTOR_BUILD";
    const string ServerDefine = "SERVER_BUILD";

    const string DirectorMenu = "Tesis/Role/Director";
    const string ActorMenu = "Tesis/Role/Actor";
    const string ServerMenu = "Tesis/Role/Server";

    // Always SERVER_BUILD: never rewritten by the role menu.
    public const string ServerProfileName = "PC Server";

    [MenuItem(DirectorMenu)] static void SwitchToDirector() => SwitchTo(Role.Director);
    [MenuItem(ActorMenu)] static void SwitchToActor() => SwitchTo(Role.Actor);
    [MenuItem(ServerMenu)] static void SwitchToServer() => SwitchTo(Role.Server);

    [MenuItem(DirectorMenu, true)] static bool ValidateDirector() => UpdateCheckmark(DirectorMenu, Role.Director);
    [MenuItem(ActorMenu, true)] static bool ValidateActor() => UpdateCheckmark(ActorMenu, Role.Actor);
    [MenuItem(ServerMenu, true)] static bool ValidateServer() => UpdateCheckmark(ServerMenu, Role.Server);

    static bool UpdateCheckmark(string menuPath, Role role)
    {
        Menu.SetChecked(menuPath, CompiledRole == role);
        return true;
    }

    // What the Editor is actually compiled as right now, read from this assembly's own
    // defines - so the checkmark shows the real state, not just what was last requested
    // (e.g. it stays on Actor if no build profile is active).
    static Role CompiledRole
    {
        get
        {
#if SERVER_BUILD
            return Role.Server;
#elif DIRECTOR_BUILD
            return Role.Director;
#else
            return Role.Actor;
#endif
        }
    }

    static void SwitchTo(Role role)
    {
        List<BuildProfile> profiles = AssetDatabase.FindAssets("t:BuildProfile")
            .Select(guid => AssetDatabase.LoadAssetAtPath<BuildProfile>(AssetDatabase.GUIDToAssetPath(guid)))
            .Where(profile => profile != null && profile.name != ServerProfileName)
            .ToList();

        if (profiles.Count == 0)
        {
            Debug.LogWarning("[RoleSwitcher] No build profiles found - nothing to switch.");
            return;
        }

        bool changed = false;
        foreach (BuildProfile profile in profiles)
        {
            string[] current = profile.scriptingDefines ?? new string[0];
            string[] updated = WithRole(current, role);
            if (updated.SequenceEqual(current))
                continue;

            profile.scriptingDefines = updated;
            EditorUtility.SetDirty(profile);
            changed = true;
        }

        if (!changed)
            return;

        AssetDatabase.SaveAssets();
        Debug.Log($"[RoleSwitcher] Role = {role}");

        if (BuildProfile.GetActiveBuildProfile() == null)
            Debug.LogWarning("[RoleSwitcher] No build profile is active, so the Editor still compiles as Actor. Activate one in File > Build Profiles for the role to apply.");

        CompilationPipeline.RequestScriptCompilation();
    }

    // Removes both role defines, then adds the one for the requested role. Other defines and
    // their order are kept as they were.
    static string[] WithRole(string[] defines, Role role)
    {
        List<string> result = defines.Where(define => define != DirectorDefine && define != ServerDefine).ToList();

        if (role == Role.Director)
            result.Add(DirectorDefine);
        else if (role == Role.Server)
            result.Add(ServerDefine);

        return result.ToArray();
    }
}
