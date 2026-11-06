using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Urbe.Game.Rendering.Buildings;

namespace Urbe.Game.Editor
{
    /// <summary>Ajustes de importación de Art/Buildings (M3-09): FBX sin materiales y en metros, atlas de 64 px sin filtrar.</summary>
    public sealed class BuildingAssetPostprocessor : AssetPostprocessor
    {
        private const string Folder = "Assets/_Project/Art/Buildings/";

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder, StringComparison.Ordinal)) return;
            var m = (ModelImporter)assetImporter;
            m.materialImportMode = ModelImporterMaterialImportMode.None;
            m.importCameras = false;
            m.importLights = false;
            m.importAnimation = false;
            m.meshCompression = ModelImporterMeshCompression.Off;
            m.isReadable = false;
            m.importBlendShapes = false;
            m.importNormals = ModelImporterNormals.Import;
            m.globalScale = 1f;
            m.useFileScale = true;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, StringComparison.Ordinal)) return;
            var t = (TextureImporter)assetImporter;
            t.textureType = TextureImporterType.Default;
            t.sRGBTexture = true;
            t.alphaSource = TextureImporterAlphaSource.FromInput;
            t.alphaIsTransparency = false;
            t.mipmapEnabled = false;
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.textureCompression = TextureImporterCompression.Uncompressed;
            t.maxTextureSize = 64;
        }
    }

    /// <summary>
    /// Importación reproducible de los edificios (M3-09): lee Buildings.manifest.json, carga las mallas LOD0 y LOD1 de cada FBX, crea el material
    /// Urbe/Vehicle con el atlas (el tinte va en el alfa del atlas, por instancia) y el <see cref="BuildingCatalog"/> que usa el render.
    /// Menú URBE / Importar edificios, o desde SceneBuilder. Los FBX los genera tools/blender/make_buildings.py; aquí no se tocan.
    /// </summary>
    public static class BuildingAssetBuilder
    {
        public const string Folder = "Assets/_Project/Art/Buildings";
        public const string ManifestPath = Folder + "/Buildings.manifest.json";
        public const string AtlasPath = Folder + "/BuildingAtlas.png";
        public const string MaterialPath = Folder + "/Building.mat";
        public const string CatalogPath = Folder + "/BuildingCatalog.asset";

        [Serializable] private class Size { public float width, depth, height; }

        [Serializable]
        private class ManifestEntry
        {
            public string id = "", fbx = "", zone = "", density = "", variant = "";
            public int level;
            public int[] footprint_cells = new int[2];
            public Size size_m = new Size();
        }

        [Serializable] private class Manifest { public ManifestEntry[] models = Array.Empty<ManifestEntry>(); }

        [MenuItem("URBE/Importar edificios")]
        public static void ImportAll()
        {
            EnsureMaterial();
            EnsureCatalog();
        }

        public static Material EnsureMaterial()
        {
            AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceSynchronousImport);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Urbe/Vehicle"));
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.shader = Shader.Find("Urbe/Vehicle");
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath));
            mat.SetColor("_Tint", Color.white);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        public static BuildingCatalog EnsureCatalog()
        {
            string json = File.ReadAllText(ManifestPath)
                .Replace("\"level\": null", "\"level\": 0").Replace("\"density\": null", "\"density\": \"\"").Replace("\"variant\": null", "\"variant\": \"\"");
            Manifest manifest = JsonUtility.FromJson<Manifest>(json);
            var catalog = AssetDatabase.LoadAssetAtPath<BuildingCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<BuildingCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            var list = new List<BuildingCatalog.Entry>();
            foreach (ManifestEntry m in manifest.models)
            {
                string fbx = Folder + "/" + m.fbx;
                AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceSynchronousImport);
                Mesh[] meshes = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().ToArray();
                Mesh? lod0 = meshes.FirstOrDefault(x => x.name.EndsWith("LOD0", StringComparison.Ordinal));
                Mesh? lod1 = meshes.FirstOrDefault(x => x.name.EndsWith("LOD1", StringComparison.Ordinal));
                if (lod0 == null || lod1 == null)
                {
                    Debug.LogError($"[URBE] Falta LOD0/LOD1 de {m.id} en {fbx} (¿se ha ejecutado tools/blender/make_buildings.py?)");
                    continue;
                }
                list.Add(new BuildingCatalog.Entry
                {
                    id = m.id,
                    zone = m.zone,
                    density = m.density == "low" ? 1 : m.density == "med" ? 2 : m.density == "high" ? 3 : 0,
                    level = m.level,
                    footprintW = m.footprint_cells.Length > 0 ? m.footprint_cells[0] : 1,
                    footprintD = m.footprint_cells.Length > 1 ? m.footprint_cells[1] : 1,
                    size = new Vector3(m.size_m.width, m.size_m.height, m.size_m.depth),
                    lod0 = lod0,
                    lod1 = lod1,
                    variant = m.variant,
                });
            }
            catalog.entries = list.ToArray();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log($"[URBE] Catálogo de edificios: {list.Count} modelos");
            return catalog;
        }
    }
}
