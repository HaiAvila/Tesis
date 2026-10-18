using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// The one place that decides which world-space panels are shown and can be pressed when
// several are up at once. Added to the Bootstrap object at startup (Bootstrap.Awake), so it
// lives for the whole app, in both builds. A new panel only needs to register.
//
// Three kinds of panel:
// - Modal: a menu you pick something in (pause menu, Favoritos, the stop-recording decision
//   panel). Only one is shown at a time - opening a modal while another is up hides the
//   older one (it isn't closed, so a pending decision isn't lost) and shows it again when
//   the newer one closes. Register with OpenModal when it appears, CloseModal when it goes.
// - Persistent: stays up while you work (the replay panel and its recorded-camera monitor,
//   the director's viewfinder). While any modal is open, every persistent panel is hidden
//   and can't be hit by the ray; when the last modal closes they come back exactly as they
//   were (including not shown at all, if they weren't). A persistent panel can pass
//   onSuspend/onResume - the replay uses it to pause, and to resume only if it was playing.
// - Guide: the tutorial. Never hidden and never hides anything - it has to stay readable
//   while you use the panel it's teaching. It asks GetObstacles for the panels it must not
//   cover and places itself beside them (TutorialController.ComputeAnchor).
//
// Hiding uses a CanvasGroup on the panel's root canvas (alpha 0, not interactable, not
// blocking raycasts), with the previous values stored and put back on restore - the panel's
// GameObject is never deactivated, so its own visible/hidden state is never touched.
//
// The pause-style "dim the world" bubble around the head while a modal is open lives in
// UpdateDim (see its comment).
public class PanelFocusManager : MonoBehaviour
{
    public enum PanelKind { Modal, Persistent, Guide }

    class Entry
    {
        public GameObject Root;
        public PanelKind Kind;
        public string Name;
        public Action OnSuspend;
        public Action OnResume;

        public bool Hidden;
        public float SavedAlpha;
        public bool SavedInteractable;
        public bool SavedBlocksRaycasts;
    }

    static readonly List<Entry> modals = new List<Entry>(); // a stack: the last one is shown
    static readonly List<Entry> persistents = new List<Entry>();
    static readonly List<Entry> guides = new List<Entry>();

    public static PanelFocusManager Instance { get; private set; }

    public static bool IsModalOpen
    {
        get
        {
            Prune();
            return modals.Count > 0;
        }
    }

    void Awake()
    {
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // Panels belong to the scene they were built in; a scene change destroys them without
    // anyone calling Close/Unregister.
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Prune();
    }

    void Update()
    {
        Prune();
        UpdateDim();
    }

    // --- Registration ------------------------------------------------------------------

    public static void OpenModal(GameObject root, string name)
    {
        if (root == null || Find(root) != null)
            return;

        Prune();
        if (modals.Count > 0)
        {
            Entry previous = modals[modals.Count - 1];
            Hide(previous);
            Debug.Log($"[PanelFocus] open modal '{name}' - hides modal '{previous.Name}' until it closes");
        }
        else
        {
            Debug.Log($"[PanelFocus] open modal '{name}' - hiding {persistents.Count} persistent panel(s)");
            foreach (Entry persistent in persistents)
                Suspend(persistent);
        }

        modals.Add(new Entry { Root = root, Kind = PanelKind.Modal, Name = name });
    }

    public static void CloseModal(GameObject root)
    {
        if (root == null)
            return; // already destroyed - Prune takes care of it

        Entry entry = Find(root);
        if (entry == null || entry.Kind != PanelKind.Modal)
            return;

        RemoveModal(entry, "closed");
    }

    public static void RegisterPersistent(GameObject root, string name, Action onSuspend = null, Action onResume = null)
    {
        if (root == null || Find(root) != null)
            return;

        var entry = new Entry { Root = root, Kind = PanelKind.Persistent, Name = name, OnSuspend = onSuspend, OnResume = onResume };
        persistents.Add(entry);
        Debug.Log($"[PanelFocus] registered persistent '{name}'");

        // Appearing while a menu is open: hidden from the start, shown when it closes.
        Prune();
        if (modals.Count > 0)
            Suspend(entry);
    }

    public static void RegisterGuide(GameObject root, string name)
    {
        if (root == null || Find(root) != null)
            return;

        guides.Add(new Entry { Root = root, Kind = PanelKind.Guide, Name = name });
        Debug.Log($"[PanelFocus] registered guide '{name}'");
    }

    // For any kind - a modal unregistering is the same as closing it.
    public static void Unregister(GameObject root)
    {
        if (root == null)
            return; // already destroyed - Prune takes care of it

        Entry entry = Find(root);
        if (entry == null)
            return;

        switch (entry.Kind)
        {
            case PanelKind.Modal:
                RemoveModal(entry, "unregistered");
                break;
            case PanelKind.Persistent:
                persistents.Remove(entry);
                Debug.Log($"[PanelFocus] unregistered persistent '{entry.Name}'");
                break;
            case PanelKind.Guide:
                guides.Remove(entry);
                Debug.Log($"[PanelFocus] unregistered guide '{entry.Name}'");
                break;
        }
    }

    // The panels a guide must not cover: the modal on top, or - with no modal open - every
    // persistent panel that's shown (active and not hidden by us).
    public static void GetObstacles(List<RectTransform> result)
    {
        result.Clear();
        Prune();

        if (modals.Count > 0)
        {
            AddIfShown(modals[modals.Count - 1], result);
            return;
        }

        foreach (Entry persistent in persistents)
            AddIfShown(persistent, result);
    }

    // --- Internals ---------------------------------------------------------------------

    static void RemoveModal(Entry entry, string how)
    {
        bool wasTop = modals.Count > 0 && modals[modals.Count - 1] == entry;
        modals.Remove(entry);

        if (!wasTop)
        {
            Debug.Log($"[PanelFocus] modal '{entry.Name}' {how} while hidden behind another");
            return;
        }

        if (modals.Count > 0)
        {
            Entry next = modals[modals.Count - 1];
            Restore(next);
            Debug.Log($"[PanelFocus] modal '{entry.Name}' {how} - modal '{next.Name}' shown again");
            return;
        }

        Debug.Log($"[PanelFocus] modal '{entry.Name}' {how} - last one; restoring {persistents.Count} persistent panel(s)");
        foreach (Entry persistent in persistents)
            Resume(persistent);
    }

    static void Suspend(Entry entry)
    {
        if (entry.Hidden)
            return;

        Hide(entry);
        entry.OnSuspend?.Invoke();
    }

    static void Resume(Entry entry)
    {
        if (!entry.Hidden)
            return;

        Restore(entry);
        entry.OnResume?.Invoke();
    }

    static void Hide(Entry entry)
    {
        if (entry.Hidden || entry.Root == null)
            return;

        CanvasGroup group = entry.Root.GetComponent<CanvasGroup>();
        if (group == null)
            group = entry.Root.AddComponent<CanvasGroup>();

        entry.SavedAlpha = group.alpha;
        entry.SavedInteractable = group.interactable;
        entry.SavedBlocksRaycasts = group.blocksRaycasts;

        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        entry.Hidden = true;
    }

    static void Restore(Entry entry)
    {
        if (!entry.Hidden)
            return;

        entry.Hidden = false;
        if (entry.Root == null)
            return;

        CanvasGroup group = entry.Root.GetComponent<CanvasGroup>();
        if (group == null)
            return;

        group.alpha = entry.SavedAlpha;
        group.interactable = entry.SavedInteractable;
        group.blocksRaycasts = entry.SavedBlocksRaycasts;
    }

    static void AddIfShown(Entry entry, List<RectTransform> result)
    {
        if (entry.Root != null && !entry.Hidden && entry.Root.activeInHierarchy)
            result.Add((RectTransform)entry.Root.transform);
    }

    static Entry Find(GameObject root)
    {
        foreach (Entry entry in modals)
            if (entry.Root == root) return entry;
        foreach (Entry entry in persistents)
            if (entry.Root == root) return entry;
        foreach (Entry entry in guides)
            if (entry.Root == root) return entry;
        return null;
    }

    // Drops panels that were destroyed without unregistering (scene changes). If that empties
    // the modal stack, the persistent panels come back as if it had closed normally.
    static void Prune()
    {
        persistents.RemoveAll(entry => entry.Root == null);
        guides.RemoveAll(entry => entry.Root == null);

        for (int i = modals.Count - 1; i >= 0; i--)
        {
            if (modals[i].Root == null)
                RemoveModal(modals[i], "destroyed");
        }
    }

    // Dim bubble - added in the next commit.
    void UpdateDim()
    {
    }
}
