using System.IO;
using FairyGUI;
using HotUpdateABTest.Demo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HotUpdateABTest.EditorTools
{
    /// <summary>
    /// Regenerates the demo scene from code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scene holds nothing but a camera, FairyGUI's stage and one component - everything else is built
    /// at runtime. Generating it rather than committing a hand-made asset keeps a Unity YAML file out of
    /// the review surface and means the scene cannot drift from what the code expects.
    /// </para>
    /// <para>
    /// Runnable headlessly, which is how it is regenerated in this repository:
    /// <c>-executeMethod HotUpdateABTest.EditorTools.DemoSceneBuilder.Build</c>.
    /// </para>
    /// </remarks>
    public static class DemoSceneBuilder
    {
        /// <summary>Where the generated scene lands.</summary>
        public const string ScenePath = "Assets/Scenes/AbTestDemo.unity";

        /// <summary>Builds the scene and saves it, replacing any existing one.</summary>
        [MenuItem("Tools/A-B Test/Rebuild demo scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraHost = new GameObject("Main Camera");
            var camera = cameraHost.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0x0D, 0x0B, 0x0A, 0xFF);
            camera.orthographic = true;
            cameraHost.tag = "MainCamera";

            // FairyGUI builds its own stage camera on demand, but having one in the scene means the demo
            // renders the instant play starts rather than one frame later.
            cameraHost.AddComponent<AudioListener>();

            var uiHost = new GameObject("UIRoot");
            uiHost.AddComponent<StageCamera>();

            var demoHost = new GameObject("AbTestDemo");
            demoHost.AddComponent<AbTestDemoBehaviour>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            AssetDatabase.Refresh();
            Debug.Log("[ABTest] demo scene written to " + ScenePath);
        }
    }
}
