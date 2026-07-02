using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


namespace Bozo.ModularCharacters
{

    [CustomEditor(typeof(BSMC_CharacterObject))]
    public class BSMC_CharacterObjectEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            BSMC_CharacterObject system = (BSMC_CharacterObject)target;
            base.OnInspectorGUI();

            if (GUILayout.Button("Update Save to Current Version"))
            {
                system.UpdateVersion();
            }
        }
    }

}