using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Sim;

namespace Urbe.Game.Tests
{
    /// <summary>M2-04b: la escena Main pinta las carreteras con RoadChunkRenderer. Captura real en docs/media/m2-carreteras.png.</summary>
    public class RoadRenderTests
    {
        private SimHost _host = null!;
        private RoadChunkRenderer _roads = null!;
        private Camera _cam = null!;

        private IEnumerator LoadShowcase(int vehicles = 120, float seconds = 40f)
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _roads = Object.FindFirstObjectByType<RoadChunkRenderer>();
            _cam = Object.FindFirstObjectByType<Camera>();
            Assert.That(_host, Is.Not.Null);
            Assert.That(_roads, Is.Not.Null, "la escena Main lleva RoadChunkRenderer");
            var rig = _cam.GetComponent<CityCameraController>();
            if (rig != null) rig.enabled = false; // la cámara se coloca a mano
            _host.SetSpeed(SimSpeed.Paused);
            var showcase = ShowcaseScenario.Create(7UL, vehicles, 3f);
            _host.Initialize(showcase.Scenario);
            yield return null;
            int ticks = Mathf.RoundToInt(seconds * SimHost.TicksPerSecond);
            for (int i = 0; i < ticks; i++) _host.StepOnce();
            _roads.Flush();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Main_PintaLasCarreteras_EnChunks()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            var host = Object.FindFirstObjectByType<SimHost>();
            var roads = Object.FindFirstObjectByType<RoadChunkRenderer>();
            Assert.That(roads, Is.Not.Null);
            roads.Flush();
            Assert.That(host.Network!.SegmentCount, Is.GreaterThan(100));
            Assert.That(roads.ChunkCount, Is.GreaterThan(10), "rejilla de 800 m en chunks de 128 m");
            Assert.That(roads.TotalTriangles, Is.GreaterThan(5000));
            Debug.Log($"[M2-04b] escena Main: {roads.ChunkCount} chunks, {roads.TotalVertices} vértices, {roads.TotalTriangles} triángulos");
            var mr = roads.GetComponentInChildren<MeshRenderer>();
            Assert.That(mr, Is.Not.Null);
            Assert.That(mr.sharedMaterial.shader.name, Is.EqualTo("Urbe/RoadSurface"));
            Assert.That(mr.sharedMaterial.GetTexture("_BaseMap"), Is.Not.Null, "atlas asignado");
        }

        [UnityTest]
        public IEnumerator Captura_Carreteras_MosaicoDeCuatroCruces()
        {
            yield return LoadShowcase();
            const int tw = 1280, th = 720;
            var mosaic = new Texture2D(tw * 2, th * 2, TextureFormat.RGBA32, false);
            var shots = new[]
            {
                (ShowcaseScenario.CrossX, -18f, 52f, 62f),        // X: avenida y calle
                (ShowcaseScenario.CrossT, 14f, 52f, 60f),         // T
                (ShowcaseScenario.CrossY, -8f, 52f, 70f),         // Y al final de la avenida
                (ShowcaseScenario.CrossCurves, 20f, 50f, 72f),    // cruce con dos calles curvas
            };
            for (int i = 0; i < shots.Length; i++)
            {
                var (focus, yaw, pitch, dist) = shots[i];
                yield return Shoot(focus, yaw, pitch, dist, tw, th, mosaic, (i % 2) * tw, (1 - i / 2) * th);
            }
            Save(mosaic, "m2-carreteras.png");
            Object.Destroy(mosaic);
        }

        private IEnumerator Shoot(Vector2 focus, float yaw, float pitch, float dist, int w, int h, Texture2D into, int ox, int oy)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            rt.Create();
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            _cam.transform.SetPositionAndRotation(new Vector3(focus.x, 0f, focus.y) - rot * Vector3.forward * dist, rot);
            _cam.nearClipPlane = 1f;
            _cam.farClipPlane = 1500f;
            _cam.targetTexture = rt;
            yield return null;
            yield return null;
            RenderTexture.active = rt;
            var tile = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tile.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tile.Apply();
            RenderTexture.active = null;
            into.SetPixels(ox, oy, w, h, tile.GetPixels());
            into.Apply();
            Object.Destroy(tile);
            _cam.targetTexture = null;
            rt.Release();
        }

        private static void Save(Texture2D tex, string fileName)
        {
            string dir = Path.Combine(Application.dataPath, "..", "..", "..", "docs", "media");
            Directory.CreateDirectory(dir);
            string path = Path.GetFullPath(Path.Combine(dir, fileName));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[M2-04b] captura " + tex.width + "x" + tex.height + " en " + path);
        }
    }
}
