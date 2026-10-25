using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DriftCity.UI
{
    /// <summary>Owns the 1024x768-referenced canvas and opens game.nui dialogs by id or key.</summary>
    public sealed class UiManager : MonoBehaviour
    {
        public static UiManager Instance { get; private set; }
        Canvas canvas;
        readonly Dictionary<string, Action<NuiView>> handlers = new Dictionary<string, Action<NuiView>>();
        readonly List<NuiView> open = new List<NuiView>();

        public Transform Root => canvas.transform;

        public static UiManager Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("Drift City UI");
            DontDestroyOnLoad(go);
            return go.AddComponent<UiManager>();
        }

        void Awake()
        {
            Instance = this;
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1024, 768);
            scaler.matchWidthOrHeight = 1f;
            gameObject.AddComponent<GraphicRaycaster>();
            if (!FindAnyObjectByType<EventSystem>())
            {
                var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
                es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
                es.AddComponent<StandaloneInputModule>();
#endif
                DontDestroyOnLoad(es);
            }
            NuiView.Clicked += OnClicked;
        }

        void OnDestroy() { NuiView.Clicked -= OnClicked; if (Instance == this) Instance = null; }

        // NATION J hotkeys the client binds on confirm/back buttons (Enter = 13, Backspace = 8): the newest open
        // dialog that has one gets the press. The chat-close bars (270000/280000) are left out: Enter talks in our chat.
        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            int key = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame ? 13 : kb.backspaceKey.wasPressedThisFrame ? 8 : 0;
#else
            int key = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ? 13 : Input.GetKeyDown(KeyCode.Backspace) ? 8 : 0;
#endif
            if (key == 0) return;
            var focused = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            if (key == 8 && focused && focused.GetComponent<InputField>()) return;   // deleting text
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var v = open[i];
                if (!v || !v.isActiveAndEnabled || v.Dialog.Id == 270000 || v.Dialog.Id == 280000) continue;
                var c = v.Hotkey(key);
                if (c == null) continue;
                UiSounds.Click();
                v.Click(c.Data.IdName);
                return;
            }
        }

        public NuiView Open(int dialogId)
        {
            var d = UiAssets.Layout.Get(dialogId);
            return d == null ? null : Track(NuiView.Create(d, canvas.transform));
        }

        /// <summary>Opens the first dialog whose control ids share the prefix (e.g. "LOGIN"), or the one owning a control id.</summary>
        public NuiView Open(string keyOrControlId)
        {
            var doc = UiAssets.Layout;
            var d = keyOrControlId.StartsWith("ID_") ? doc.FindByControl(keyOrControlId) : doc.Dialogs.FirstOrDefault(x => x.Key == keyOrControlId);
            if (d == null) { Debug.LogWarning("no dialog " + keyOrControlId); return null; }
            return Track(NuiView.Create(d, canvas.transform));
        }

        NuiView Track(NuiView v) { open.Add(v); return v; }

        public bool IsOpen(NuiView v) => v && open.Contains(v);
        public IReadOnlyList<NuiView> OpenViews => open;
        public NuiView Find(int dialogId) => open.FirstOrDefault(v => v && v.Dialog.Id == dialogId);

        /// <summary>Renders UI over the main camera into a texture (works in batch mode where overlay canvases aren't captured).</summary>
        public Texture2D Capture(Camera cam, int width = 1024, int height = 768)
        {
            var rt = new RenderTexture(width, height, 24);
            var prevMode = canvas.renderMode;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = Mathf.Max(cam.nearClipPlane * 2f, 0.5f);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            Canvas.ForceUpdateCanvases();
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = prevTarget;
            canvas.renderMode = prevMode;
            rt.Release();
            return tex;
        }

        public void Close(NuiView v)
        {
            open.RemoveAll(x => !x || x == v);   // also forget views destroyed some other way
            if (!v) return;
            Destroy(v.gameObject);
        }

        public void CloseAll()
        {
            foreach (var v in open) if (v) Destroy(v.gameObject);
            open.Clear();
        }

        /// <summary>Registers a click handler for a control id (replaces any previous one).</summary>
        public void On(string controlId, Action<NuiView> handler) => handlers[controlId] = handler;
        public void Off(string controlId) => handlers.Remove(controlId);

        void OnClicked(NuiView view, string id)
        {
            if (handlers.TryGetValue(id, out var h)) h(view);
            else if (id.EndsWith("_CLOSE") || id.EndsWith("_EXIT") || id.EndsWith("_CANCEL")) Close(view); // generic close buttons
        }

        /// <summary>A plain fullscreen RawImage layer (used for 3D previews rendered to texture).</summary>
        public RawImage Layer(string name, int siblingIndex = -1)
        {
            var rt = NuiView.NewRect(name, canvas.transform);
            NuiView.Stretch(rt);
            if (siblingIndex >= 0) rt.SetSiblingIndex(siblingIndex);
            var img = rt.gameObject.AddComponent<RawImage>();
            img.raycastTarget = false;
            return img;
        }
    }
}
