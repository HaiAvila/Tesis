using UnityEditor;
using UnityEngine;

// A Calibrar button for the app's avatar (AvatarPoseSync, Bootstrap → ActorAvatar), plus what
// this machine is doing with it. Works whether or not the Game view has focus.
[CustomEditor(typeof(AvatarPoseSync))]
public class AvatarPoseSyncInspector : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("El botón funciona en Play mode, en la máquina que tiene los pods.", MessageType.Info);
            return;
        }

        var sync = (AvatarPoseSync)target;
        string role = sync.IsSending ? "envía la pose (tiene los pods y está calibrado)"
            : sync.IsReceiving ? "recibe la pose de otra máquina"
            : sync.HasPodsHere ? "tiene pods, falta calibrar"
            : "sin pods y sin pose recibida";
        EditorGUILayout.LabelField($"Esta máquina: {role}");
        if (sync.podAvatar != null && sync.podAvatar.CurrentPhase != PodAvatar.Phase.Idle)
            EditorGUILayout.LabelField($"Calibración: {sync.podAvatar.CurrentPhase} ({sync.podAvatar.PhaseTimeLeft:F0} s)");

        using (new EditorGUI.DisabledScope(!sync.HasPodsHere))
        {
            if (GUILayout.Button("Calibrar", GUILayout.Height(28)))
                sync.StartCalibration();
        }

        Repaint();
    }
}
