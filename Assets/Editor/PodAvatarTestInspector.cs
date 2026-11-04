using UnityEditor;
using UnityEngine;

// A Calibrar button for PodAvatarTest in the Inspector: it works whether or not the Game view
// has focus (the Input System drops the keyboard while it doesn't).
[CustomEditor(typeof(PodAvatarTest))]
public class PodAvatarTestInspector : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("El botón funciona en Play mode.", MessageType.Info);
            return;
        }

        var test = (PodAvatarTest)target;
        PodAvatar podAvatar = test.podAvatar;
        if (podAvatar != null)
            EditorGUILayout.LabelField($"Calibración: {podAvatar.CurrentPhase}" + (podAvatar.CurrentPhase != PodAvatar.Phase.Idle ? $" ({podAvatar.PhaseTimeLeft:F0} s)" : ""));

        if (GUILayout.Button("Calibrar (K)", GUILayout.Height(28)))
            test.StartCalibration();

        Repaint();
    }
}
