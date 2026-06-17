// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER && UNITY_EDITOR
using HutongGames.PlayMakerEditor;
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Sonity.PlayMaker.Internal {

    [CustomActionEditor(typeof(SonityMusicPlay))]
    public class SonityPlayMusicEditor : SonityPlaySoundEditor {
        public override bool OnGUI() {
            var childTarget = m_Target as SonityMusicPlay;
            m_OptionalEditField1 = nameof(childTarget.stopAllOtherMusic);
            m_OptionalEditField2 = nameof(childTarget.allowFadeout);

            base.OnGUI();
            return GUI.changed;
        }
    }

    [CustomActionEditor(typeof(SonityUIPlaySound))]
    public class SonityPlayUISoundEventActionEditor : SonityPlaySoundEditor {
    }

    [CustomActionEditor(typeof(SonityPlaySoundAtPositionVector3))]
    public class SonityPlaySoundEventAtVector3Editor : SonityPlaySoundEditor {

        public override bool OnGUI() {
            var childTarget = m_Target as SonityPlaySoundAtPositionVector3;
            m_OptionalEditField1 = nameof(childTarget.playbackPositionVector3);

            base.OnGUI();
            return GUI.changed;
        }

    }

    [CustomActionEditor(typeof(SonityPlaySoundAtPositionTransform))]
    public class SonityPlaySoundAtTransformEditor : SonityPlaySoundEditor {

        public override bool OnGUI() {
            var childTarget = m_Target as SonityPlaySoundAtPositionTransform;
            m_OptionalEditField1 = nameof(childTarget.playbackPositionTransform);

            base.OnGUI();
            return GUI.changed;
        }
    }

    [CustomActionEditor(typeof(SonityPlaySound))]
    public class SonityPlaySoundEditor : CustomActionEditor {
        private string m_ParamTypeName = "type";
        private string m_ParamModeName = "mode";
        private string m_ParamVarName = "value";

        protected SonityPlaySound m_Target;
        protected string m_OptionalEditField1 = string.Empty;
        protected string m_OptionalEditField2 = string.Empty;

        public override void OnEnable() {
            m_Target = target as SonityPlaySound;
        }

        public override bool OnGUI() {
#region EditFields
            DrawDefaultEditFields();
#endregion

            if (m_Target.useParameters.Value == false) {
                SetZeroParameters();
                return GUI.changed;
            }

#region If parameters are enabled
            EditField(nameof(m_Target.numberOfParameters));

            SetMinNumOfParameters(1);
            SyncSoundParameters();

            ParameterDisplayOptions();
#endregion
            return GUI.changed;
        }

        protected virtual void DrawDefaultEditFields() {

            if (!m_Target.HideSoundEvent())
                EditField(nameof(m_Target.fsmSoundEvent));

            if (!m_Target.HideGameObjectReference())
                EditField(nameof(m_Target.gameObjectOverride));

            if (!m_Target.HideFsmNameReference())
                EditField(nameof(m_Target.fsmName));

            if (m_OptionalEditField1 != string.Empty)
                EditField(m_OptionalEditField1);

            if (m_OptionalEditField2 != string.Empty)
                EditField(m_OptionalEditField2);

            EditField(nameof(m_Target.soundTag));
            EditField(nameof(m_Target.useParameters));
        }

        private void ParameterDisplayOptions() {
            m_Target.showFoldOut.Value = EditorGUILayout.Foldout(m_Target.showFoldOut.Value, "Show parameters");

            if (m_Target.showFoldOut.Value) {
                for (int i = 0; i < m_Target.NumberOfParameters; i++) {
                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    GUILayout.Label($"{(i + 1)}). {CreateFieldLabel(m_ParamTypeName + i, x => x.ToUpper())}", EditorStyles.boldLabel);
                    EditField(m_ParamTypeName + (i));
                    EditField(m_ParamModeName + (i));
                    EditField(m_ParamVarName + (i));
                    GUILayout.Space(10);
                }
                m_Target.SetFsmVarToSoundParameter();
            }
        }

        private string CreateFieldLabel(string fieldName, Func<string, string> action = null) {
            var typeName = AddSpaceToCapitalLetters(GetParameterTypeAsString(m_Target, fieldName));
            typeName = action?.Invoke(typeName);
            return typeName;
        }

        /// <summary>
        /// Sync input parameter values with target <see cref="SonityPlaySound"/>. Value is clamped to MaxAmountOfParameters in target.
        /// </summary>
        private void SyncSoundParameters() {
            try {
                if (m_Target.numberOfParameters.Value != m_Target.NumberOfParameters) {
                    m_Target.NumberOfParameters = m_Target.numberOfParameters.Value;
                    m_Target.numberOfParameters.Value = m_Target.NumberOfParameters;
                }
            } catch {
                Debug.Log("UpdateAndClampSoundParameters()" + " error in method. GL!");
            }
        }

        private void SetMinNumOfParameters(int parameters) {
            if (m_Target.NumberOfParameters < parameters) {
                m_Target.NumberOfParameters = parameters;
            }
        }

        private void SetZeroParameters() {
            if (m_Target.NumberOfParameters > 0)
                m_Target.NumberOfParameters = 0;
        }

#region String manipulation methods
        private string GetParameterTypeAsString(object target, string fieldName) {
            var value = target.GetType().GetField(fieldName).GetValue(target);
            if (value != null) {
                return value.ToString();
            }
            return "";
        }

        private string AddSpaceToCapitalLetters(string _string) =>
                string.Concat(_string.Select(x => Char.IsUpper(x) ? " " + x : x.ToString())).TrimStart(' ');
#endregion
    }
}
#endif
