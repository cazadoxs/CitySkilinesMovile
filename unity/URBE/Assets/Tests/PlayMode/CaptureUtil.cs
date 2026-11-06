using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// Capturas sin ventana (batchmode): la cámara dibuja la escena en un RenderTexture y el panel de UI Toolkit se pinta encima en el
    /// mismo RenderTexture. 1688x780 equivale a un móvil horizontal de 844x390 dp a 2x.
    /// </summary>
    public static class CaptureUtil
    {
        public static string MediaPath(string fileName)
        {
            string dir = Path.Combine(Application.dataPath, "..", "..", "..", "docs", "media");
            Directory.CreateDirectory(dir);
            return Path.GetFullPath(Path.Combine(dir, fileName));
        }

        public static IEnumerator Shot(Camera cam, UIDocument uiDoc, int w, int h, string fileName, System.Action? whileRendered = null)
        {
            // Se clona el PanelSettings: cambiar targetTexture en el asset compartido deja la UI sin recibir clics en las pruebas posteriores.
            PanelSettings original = uiDoc.panelSettings;
            var panel = Object.Instantiate(original);
            uiDoc.panelSettings = panel;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            bool camWasEnabled = cam.enabled;
            // Primero el panel se dibuja en una textura auxiliar hasta que su diseño al tamaño de la captura es estable (sin fantasmas de diseños anteriores).
            var scratch = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            scratch.Create();
            panel.targetTexture = scratch;
            for (int i = 0; i < 4; i++) yield return null;
            cam.targetTexture = rt;
            yield return null;
            yield return null;
            cam.enabled = false;
            panel.clearColor = false;
            panel.targetTexture = rt;
            yield return null;
            yield return null;
            whileRendered?.Invoke(); // el panel ya tiene su diseño al tamaño de la captura: aquí se comprueban solapes
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            panel.targetTexture = null;
            cam.targetTexture = null;
            cam.enabled = camWasEnabled;
            // El PanelSettings clonado se queda en el documento hasta que se descarga la escena (restaurar el original dejaba la UI sin clics).
            File.WriteAllBytes(MediaPath(fileName), tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
            scratch.Release();
        }
    }
}
