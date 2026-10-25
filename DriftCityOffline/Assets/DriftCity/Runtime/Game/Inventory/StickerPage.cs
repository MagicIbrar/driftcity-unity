using System.IO;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The inventory's Sticker page (INVEN 3100000 / NDLGINVEN 4830000) as the custom-mark page (Game/CustomMarks.cs): the
    /// car turns in the page's view with the mark on its doors; the images in the Stickers folder next to the saves are
    /// listed (click one to use it), "Import image file..." takes any path (MsgBox 3230000), "Remove mark" clears it.
    /// </summary>
    public static partial class InventoryWindow
    {
        const int StickerRows = 5;

        static void SetupStickers()
        {
            if (!pageA) return;
            var anchor = pageA.Control("ID_INVEN_STICKER_BACKGROUND");
            if (!anchor) return;
            preview?.Show(P.Cars[Mathf.Clamp(carSlot, 0, P.Cars.Count - 1)]);
            DrawStickers();
        }

        static void DrawStickers()
        {
            var anchor = pageA ? pageA.Control("ID_INVEN_STICKER_BACKGROUND") : null;
            if (!anchor) return;
            var head = OriginalMenus.Cell(anchor, "mark head", new Vector2(10, 300), new Vector2(330, 18));
            bool has = CustomMarks.Own();
            OriginalMenus.Caption(head, "text", has ? "Your mark (on both doors)" : "Custom mark: none", "Calibri 12 700", new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleLeft);
            var files = CustomMarks.FolderImages();
            for (int i = 0; i < StickerRows; i++)
            {
                var row = OriginalMenus.Cell(anchor, "mark file " + i, new Vector2(10, 320 + i * 15), new Vector2(330, 15));
                if (i >= files.Count) { OriginalMenus.Caption(row, "text", i == 0 ? "(put PNG / JPG images in the Stickers folder)" : "", "Calibri 11 400", new Color(0.7f, 0.7f, 0.7f), TextAnchor.MiddleLeft); continue; }
                string path = files[i];
                OriginalMenus.Caption(row, "text", "> " + Path.GetFileName(path), "Calibri 11 400", Color.white, TextAnchor.MiddleLeft);
                OriginalMenus.Clickable(row, () => ImportMark(path));
            }
            var import = OriginalMenus.Cell(anchor, "mark import", new Vector2(10, 398), new Vector2(160, 16));
            OriginalMenus.Caption(import, "text", "[ Import image file... ]", "Calibri 12 700", new Color(0.55f, 0.85f, 1f), TextAnchor.MiddleLeft);
            OriginalMenus.Clickable(import, () => MsgBox.Prompt("Image file (PNG / JPG, up to 4 MB):", ImportMark, Path.Combine(CustomMarks.Folder, "mark.png"), MsgBox.Confirm.Ok, 260));
            var clear = OriginalMenus.Cell(anchor, "mark clear", new Vector2(190, 398), new Vector2(150, 16));
            OriginalMenus.Caption(clear, "text", has ? "[ Remove mark ]" : "", "Calibri 12 700", new Color(1f, 0.6f, 0.5f), TextAnchor.MiddleRight);
            OriginalMenus.Clickable(clear, () => { if (CustomMarks.Own()) CustomMarks.Clear(_ => AfterMark()); });
        }

        static void ImportMark(string path)
        {
            CustomMarks.Import(path, (ok, error) =>
            {
                if (!ok) { UiPopup.Message(error ?? "The image could not be used."); return; }
                AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL");
                AfterMark();
            });
        }

        static void AfterMark()
        {
            if (!frame || page != Page.Sticker) return;
            preview?.Show(P.Cars[Mathf.Clamp(carSlot, 0, P.Cars.Count - 1)]);
            DrawStickers();
        }
    }
}
