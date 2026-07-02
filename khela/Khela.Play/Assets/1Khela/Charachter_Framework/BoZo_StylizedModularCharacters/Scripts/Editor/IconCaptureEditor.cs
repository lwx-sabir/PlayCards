using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Bozo.ModularCharacters
{
    [CustomEditor(typeof(IconCapture))]
    public class IconCaptureEditor : Editor
    {

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            IconCapture icon = (IconCapture)target;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Take ScreenShot"))
            {
                icon.Capture();
            }
            GUILayout.EndHorizontal();
        }
    }
}
