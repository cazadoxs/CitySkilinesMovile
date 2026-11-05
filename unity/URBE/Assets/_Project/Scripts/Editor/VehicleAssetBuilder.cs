using System.Linq;
using UnityEditor;
using UnityEngine;
using Urbe.Game.Rendering.Vehicles;

namespace Urbe.Game.Editor
{
    /// <summary>
    /// Importación y montaje de los vehículos (M2-07): ajustes del FBX y del atlas, material "Urbe/Vehicle" y el <see cref="VehicleModelSet"/>.
    /// Los ficheros de Art/Vehicles los genera tools/blender/make_vehicles.py; este script no los modifica.
    /// </summary>
    public sealed class VehicleAssetPostprocessor : AssetPostprocessor
    {
        private const string Folder = "Assets/_Project/Art/Vehicles/";

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var m = (ModelImporter)assetImporter;
            m.materialImportMode = ModelImporterMaterialImportMode.None;
            m.importCameras = false;
            m.importLights = false;
            m.importAnimation = false;
            m.meshCompression = ModelImporterMeshCompression.Off;
            m.isReadable = true; // pequeño; las pruebas leen UVs y vértices
            m.importBlendShapes = false;
            m.importNormals = ModelImporterNormals.Import;
            m.globalScale = 1f;
            m.useFileScale = true;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
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

    public static class VehicleAssetBuilder
    {
        public const string Folder = "Assets/_Project/Art/Vehicles";
        public const string FbxPath = Folder + "/Vehicles.fbx";
        public const string AtlasPath = Folder + "/VehicleAtlas.png";
        public const string MaterialPath = Folder + "/Vehicle.mat";
        public const string SetPath = Folder + "/VehicleModels.asset";

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

        public static VehicleModelSet EnsureModelSet()
        {
            AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceSynchronousImport);
            Mesh[] meshes = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<Mesh>().ToArray();
            var set = AssetDatabase.LoadAssetAtPath<VehicleModelSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<VehicleModelSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            var entries = new VehicleModelSet.Entry[VehicleModelIds.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                string n = VehicleModelIds.Names[i];
                entries[i] = new VehicleModelSet.Entry
                {
                    name = n,
                    lod0 = meshes.FirstOrDefault(m => m.name == $"Veh_{n}_LOD0"),
                    lod1 = meshes.FirstOrDefault(m => m.name == $"Veh_{n}_LOD1"),
                };
                if (entries[i].lod0 == null || entries[i].lod1 == null)
                    Debug.LogError($"[URBE] Falta la malla de {n} en {FbxPath} (¿se ha ejecutado tools/blender/make_vehicles.py?)");
            }
            set.entries = entries;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return set;
        }
    }
}
