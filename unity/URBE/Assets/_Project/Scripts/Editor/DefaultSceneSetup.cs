using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Urbe.Game.Editor
{
    /// <summary>
    /// Main.unity es la escena por defecto: es la única de la build y, si el Editor se abre con una escena vacía sin guardar,
    /// se abre Main. Así el proyecto se juega con Play sin pasos previos. (No se usa playModeStartScene: en batchmode bloquea el
    /// ejecutor de pruebas al reemplazar la escena de la prueba.)
    /// </summary>
    [InitializeOnLoad]
    public static class DefaultSceneSetup
    {
        static DefaultSceneSetup()
        {
            EditorApplication.delayCall += Apply;
        }

        public static void Apply()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneBuilder.ScenePath) == null) return; // aún no generada: URBE > Generar escena Main
            Scene active = SceneManager.GetActiveScene();
            if (SceneManager.sceneCount == 1 && string.IsNullOrEmpty(active.path) && !active.isDirty && active.rootCount == 0 && !UnityEngine.Application.isBatchMode)
                EditorSceneManager.OpenScene(SceneBuilder.ScenePath, OpenSceneMode.Single);
        }
    }
}
