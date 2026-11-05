using UnityEngine;

// On the VoiceClient prefab. Normcore creates a copy of it on every machine, and one that arrives
// from another machine lands in whatever scene is active: without this, the first scene change
// destroys it, and that person's voice with it.
public class VoiceClient : MonoBehaviour
{
    void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }
}
