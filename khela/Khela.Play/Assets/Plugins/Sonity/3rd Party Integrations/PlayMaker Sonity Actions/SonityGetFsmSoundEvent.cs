// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using HutongGames.PlayMaker;
using UnityEngine;
using TooltipAttribute = HutongGames.PlayMaker.TooltipAttribute;
using Sonity.PlayMaker.Internal;

namespace Sonity.PlayMaker {

    [ActionCategory(ActionCategory.StateMachine)]
    [HelpURL("https://sonityaudio.github.io")]
    [ActionTarget(typeof(PlayMakerFSM), "gameObject,fsmName")]
    [Tooltip("Get the value of a Sound Event Variable from another FSM.")]
    public class SonityGetFsmSoundEvent : FsmStateAction {

        [RequiredField]
        [Tooltip("The GameObject that owns the FSM.")]
        public FsmOwnerDefault gameObject;

        [UIHint(UIHint.FsmName)]
        [Tooltip("Optional name of FSM on Game Object")]
        public FsmString fsmName;

        [RequiredField]
        [UIHint(UIHint.FsmObject)]
        [ObjectType(typeof(SoundEvent))]
        [Tooltip("The name of the FSM variable to get.")]
        public FsmString variableName;

        [RequiredField]
        [UIHint(UIHint.Variable)]
        [ObjectType(typeof(SoundEvent))]
        [Tooltip("Store the value in a Float variable in this FSM.")]
        public FsmObject storeValue;

        [Tooltip("Repeat every frame. Useful if the value is changing.")]
        public bool everyFrame;

        private GameObject goLastFrame;
        private string fsmNameLastFrame;
        protected PlayMakerFSM fsm;

        public override void Reset() {
            gameObject = null;
            fsmName = "";
            variableName = "";
            storeValue = null;
            everyFrame = false;
        }

        public override void OnEnter() {
            DoGetFsmVariable();

            if (!everyFrame) {
                Finish();
            }
        }

        public override void OnUpdate() {
            DoGetFsmVariable();
        }

        private void DoGetFsmVariable() {
            var go = Fsm.GetOwnerDefaultTarget(gameObject);
            if (go == null) {
                return;
            }

            if (go != goLastFrame || fsmName.Value != fsmNameLastFrame) {
                goLastFrame = go;
                fsmNameLastFrame = fsmName.Value;
                // only get the fsm component if go or fsm name has changed
                fsm = SonityActionHelpers.GetGameObjectFsm(go, fsmName.Value);
            }

            if (fsm == null || storeValue == null) {
                return;
            }

            var fsmVar = fsm.FsmVariables.GetFsmObject(variableName.Value);

            if (fsmVar != null) {
                storeValue.Value = fsmVar.Value;
            }
        }
    }
}
#endif