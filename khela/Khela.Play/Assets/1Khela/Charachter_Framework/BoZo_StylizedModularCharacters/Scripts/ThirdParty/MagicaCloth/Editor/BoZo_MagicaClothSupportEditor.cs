using UnityEditor;
using UnityEngine;


namespace Bozo.ModularCharacters
{

    [CustomEditor(typeof(BoZo_MagicaClothSupport))]
    [CanEditMultipleObjects]
    public class BoZo_MagicaClothSupportEditor : Editor
    {
        public override void OnInspectorGUI()
        {
#if MAGICACLOTH2
            BoZo_MagicaClothSupport ob = (BoZo_MagicaClothSupport)target;
            Color originalColor = GUI.color;

            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("InitalizeOnStart"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("type"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("skinnedMeshRenderer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("disableByTag"));



            EditorGUILayout.Space(10);

            if (ob.type == BoZo_MagicaClothSupport.ClothType.Mesh)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("influenceMap"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("reductionSetting"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("collisionSize"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("collisionCurve"));
            }
            else if (ob.type == BoZo_MagicaClothSupport.ClothType.Bone || ob.type == BoZo_MagicaClothSupport.ClothType.Spring)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("boneReferenceByString"));
                if (ob.boneReferenceByString)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("rootBonesString"));
                }
                else
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("rootBones"));
                }


                EditorGUILayout.PropertyField(serializedObject.FindProperty("collisionSize"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("collisionCurve"));
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("clothPreset"));

            serializedObject.ApplyModifiedProperties();
            //base.OnInspectorGUI();
            return;
#endif
            EditorGUILayout.LabelField("MagicaCloth2 Not Installed");
        }
    }

}
