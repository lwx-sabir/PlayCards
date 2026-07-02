using UnityEngine;
using UnityEditor;

namespace Bozo.ModularCharacters
{
#if UNITY_EDITOR

    [InitializeOnLoad]
    public static class BoZo_AutoIncludeShaders
    {
        static BoZo_AutoIncludeShaders()
        {
            AddShaderToAlwaysIncluded("Shader Graphs/BoZo_ColorEditor");
            AddShaderToAlwaysIncluded("BoZo/BakeTexture");
            AddShaderToAlwaysIncluded("Hidden/BoZo_DecalProjector");
        }
        public static void AddShaderToAlwaysIncluded(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return;
            }

            // 1. Get the GraphicsSettings object
            Object graphicsSettingsAsset = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettingsAsset == null) return;

            SerializedObject graphicsSettings = new SerializedObject(graphicsSettingsAsset);
            SerializedProperty alwaysIncludedShaders = graphicsSettings.FindProperty("m_AlwaysIncludedShaders");

            // 2. Check if the shader is already in the list to avoid duplicates
            bool exists = false;
            for (int i = 0; i < alwaysIncludedShaders.arraySize; i++)
            {
                SerializedProperty element = alwaysIncludedShaders.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue == shader)
                {
                    exists = true;
                    break;
                }
            }

            // 3. Add the shader if it's missing
            if (!exists)
            {
                int index = alwaysIncludedShaders.arraySize;
                alwaysIncludedShaders.InsertArrayElementAtIndex(index);
                alwaysIncludedShaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;

                graphicsSettings.ApplyModifiedProperties();
                Debug.Log($"Successfully added {shaderName} to Always Included Shaders.");
            }
            else
            {

            }
        }
    }
#endif
}
