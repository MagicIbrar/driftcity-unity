using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>Plain IMGUI message shown when the installed client can't be found (no game UI is available then).</summary>
    public sealed class MissingInstallMessage : MonoBehaviour
    {
        public string Text;

        void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            style.normal.textColor = Color.white;
            GUI.color = new Color(0, 0, 0, 0.85f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(40, 40, Screen.width - 80, Screen.height - 80), Text, style);
            if (GUI.Button(new Rect(Screen.width / 2f - 80, Screen.height - 120, 160, 44), "Quit")) Application.Quit();
        }
    }
}
