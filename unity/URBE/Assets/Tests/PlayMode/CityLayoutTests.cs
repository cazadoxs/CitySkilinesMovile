using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Urbe.Game.Sim;
using Urbe.Game.Tools.CityTools;
using Urbe.Game.UI;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// M3-07 y M3-08: el HUD de la ciudad no se solapa ni se sale de la pantalla en móvil horizontal, móvil vertical y PC (se hace el diseño al tamaño
    /// de cada captura), y los botones tienen objetivos de 48 dp. Guarda capturas del móvil vertical y de PC con la barra de zonas abierta.
    /// </summary>
    public class CityLayoutTests : InputTestFixture
    {
        private static readonly string[] TopElements =
            { "money-chip", "date-chip", "pop-chip", "milestone-chip", "rci", "btn-pause", "btn-x1", "btn-x2", "btn-x3" };

        private static Rect Rect(VisualElement e) => e.worldBound;

        private static void AssertNoOverlap(VisualElement root, IList<string> names, string label)
        {
            var rects = new List<(string, Rect)>();
            foreach (string n in names)
            {
                VisualElement? e = root.Q<VisualElement>(n);
                Assert.That(e, Is.Not.Null, n);
                if (e!.resolvedStyle.display == DisplayStyle.None) continue;
                rects.Add((n, Rect(e)));
            }
            float w = root.panel.visualTree.resolvedStyle.width, h = root.panel.visualTree.resolvedStyle.height;
            for (int i = 0; i < rects.Count; i++)
            {
                Rect a = rects[i].Item2;
                Assert.That(a.xMin, Is.GreaterThanOrEqualTo(-1f), label + ": " + rects[i].Item1 + " se sale por la izquierda");
                Assert.That(a.xMax, Is.LessThanOrEqualTo(w + 1f), label + ": " + rects[i].Item1 + " se sale por la derecha (" + a.xMax + " de " + w + ")");
                Assert.That(a.yMax, Is.LessThanOrEqualTo(h + 1f), label + ": " + rects[i].Item1 + " se sale por abajo");
                for (int j = i + 1; j < rects.Count; j++)
                {
                    Rect b = rects[j].Item2;
                    bool overlap = a.xMin < b.xMax - 1f && b.xMin < a.xMax - 1f && a.yMin < b.yMax - 1f && b.yMin < a.yMax - 1f;
                    Assert.That(overlap, Is.False, label + ": " + rects[i].Item1 + " " + a + " se solapa con " + rects[j].Item1 + " " + b);
                }
            }
        }

        private IEnumerator Check(int w, int h, string label, string? file)
        {
            var doc = Object.FindFirstObjectByType<UIDocument>();
            var cam = Object.FindFirstObjectByType<Camera>();
            yield return CaptureUtil.Shot(cam, doc, w, h, file ?? "m3-layout-tmp.png", () =>
            {
                VisualElement root = doc.rootVisualElement;
                AssertNoOverlap(root, TopElements, label + " arriba");
                AssertNoOverlap(root, new[] { "btn-build", "cat-roads", "cat-zones", "cat-utilities", "cat-services", "cat-demolish" }, label + " barra de categorías");
                // El panel de zonas no tapa la barra de categorías ni la superior.
                VisualElement sub = root.Q<VisualElement>("sub-panel");
                VisualElement bar = root.Q<VisualElement>("category-bar");
                Assert.That(Rect(sub).yMax, Is.LessThanOrEqualTo(Rect(bar).yMin + 1f), label + ": el panel de herramientas queda sobre la barra " + Rect(sub) + " " + Rect(bar) + " build " + Rect(root.Q<VisualElement>("btn-build")) + " bottom-row " + Rect(root.Q<VisualElement>("bottom-row")));
                Assert.That(Rect(sub).yMin, Is.GreaterThanOrEqualTo(Rect(root.Q<VisualElement>("top-bar")).yMax - 1f), label + ": el panel no pisa la barra superior");
                foreach (Button b in sub.Query<Button>().ToList())
                {
                    if (b.resolvedStyle.display == DisplayStyle.None) continue;
                    Assert.That(b.resolvedStyle.height, Is.GreaterThanOrEqualTo(47f), label + ": " + b.name + " mide 48 dp de alto");
                    Rect br = Rect(b), sr = Rect(sub);
                    Assert.That(br.xMin >= sr.xMin - 1f && br.xMax <= sr.xMax + 1f, label + ": " + b.name + " " + br + " queda dentro del panel " + sr);
                }
                // El panel de herramientas no pisa el consejo, el aviso ni el panel de edificio cuando se ven.
                foreach (string other in new[] { "tip-panel", "building-panel", "toast", "menu-message" })
                {
                    VisualElement o = root.Q<VisualElement>(other);
                    if (o.resolvedStyle.display == DisplayStyle.None || !sub.ClassListContains("open")) continue;
                    Rect a = Rect(sub), c = Rect(o);
                    bool overlap = a.xMin < c.xMax - 1f && c.xMin < a.xMax - 1f && a.yMin < c.yMax - 1f && c.yMin < a.yMax - 1f;
                    Assert.That(overlap, Is.False, label + ": el panel de herramientas " + a + " pisa " + other + " " + c);
                }
            });
            if (file == null) File.Delete(CaptureUtil.MediaPath("m3-layout-tmp.png"));
        }

        [UnityTest]
        public IEnumerator Hud_SinSolapes_EnMovilHorizontal_Vertical_Y_Pc()
        {
            SceneManager.LoadScene("Main");
            for (int i = 0; i < 4; i++) yield return null;
            var host = Object.FindFirstObjectByType<SimHost>();
            host.SetSpeed(SimSpeed.Paused);
            var menu = Object.FindFirstObjectByType<BuildMenuController>();
            menu.Open();
            menu.SelectCategory(BuildCategory.Zones);
            for (int i = 0; i < 4; i++) yield return null;
            yield return Check(1688, 780, "móvil horizontal 844x390", null);
            yield return Check(780, 1688, "móvil vertical 390x844", "m3-hud-vertical.png");
            yield return Check(1920, 1080, "PC 1920x1080", null);
            menu.SelectCategory(BuildCategory.Services);
            for (int i = 0; i < 3; i++) yield return null;
            yield return Check(1688, 780, "móvil horizontal, servicios", null);
        }
    }
}
