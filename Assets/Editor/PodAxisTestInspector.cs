using UnityEditor;
using UnityEngine;

// Buttons for PodAxisTest in the Inspector: the same actions as its keys (Z, X, C, 1/2/3), but
// they work whether or not the Game view has focus - the Input System drops the keyboard
// while it doesn't (Play Mode Input Behavior: "respect Game view focus").
[CustomEditor(typeof(PodAxisTest))]
public class PodAxisTestInspector : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var test = (PodAxisTest)target;
        EditorGUILayout.Space();

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Los botones funcionan en Play mode.", MessageType.Info);
            return;
        }

        PodReceiver receiver = test.receiver;
        if (receiver != null)
            EditorGUILayout.LabelField($"Conversión {receiver.conversion}   ·   espejo X {receiver.flipX}, Y {receiver.flipY}, Z {receiver.flipZ}");

        if (GUILayout.Button("Paso 1: poner en cero — plano, USB a la derecha (Z)", GUILayout.Height(28)))
            test.Zero();
        if (GUILayout.Button("Paso 2: USB apuntando hacia arriba (X)", GUILayout.Height(28)))
            test.Step2();
        if (GUILayout.Button("Cambiar conversión (C)", GUILayout.Height(28)))
            test.CycleConversion();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Espejo X (1)"))
            test.ToggleFlip(0);
        if (GUILayout.Button("Espejo Y (2)"))
            test.ToggleFlip(1);
        if (GUILayout.Button("Espejo Z (3)"))
            test.ToggleFlip(2);
        EditorGUILayout.EndHorizontal();

        // Keep the line above current while playing.
        Repaint();
    }
}
